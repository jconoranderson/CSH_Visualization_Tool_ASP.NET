using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using SleepVisualizationTool.Models;

namespace SleepVisualizationTool.Services;

/// <summary>
/// Loads sleep records from the CSV formats supported by the legacy Flask app.
/// Handles both the raw "Details,Name" export and the already-normalised form.
/// </summary>
public sealed class SleepCsvLoader
{
    private static readonly Regex DashRegex = new("[–—−]", RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"Date[:\s]*([^\n\r]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StartLineRegex = new(@"Start time[^\n]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndLineRegex = new(@"End time[^\n]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InterruptionsRegex = new(@"INTERUPPTIONS TOTAL #\s*[:]*\s*(\d+)|INTERRUPTIONS(?: TOTAL)?\s*#?\s*[:]*\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HoursRegex = new(@"Hours[:\s]*([0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MinutesRegex = new(@"Minutes[:\s]*([0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InterruptionHeaderRegex = new(@"INTER+UP?TIONS?\s+TOTAL\s*#.*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeRegex = new(@"(\d{1,2}:\d{2})", RegexOptions.Compiled);
    private static readonly Regex CheckedPeriodRegex = new(@"[\(\[\{]\s*[xX]\s*[\)\]\}]\s*(A\.?M\.?|P\.?M\.?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnyPeriodRegex = new(@"\b(A\.?M\.?|P\.?M\.?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] RawNameHeaders = { "Name", "Resident Name", "Resident", "ResidentName", "Client Name", "Participant Name" };
    private static readonly string[] RawDetailsHeaders = { "Details", "Progress Note", "Progress Note Note", "Progress Note Text", "Note" };

    private static readonly string[] ProcessedNameHeaders = { "Name", "Resident Name", "Resident" };
    private static readonly string[] ProcessedStartHeaders = { "start_dt", "start", "start_time", "start_datetime" };
    private static readonly string[] ProcessedEndHeaders = { "end_dt", "end", "end_time", "end_datetime" };
    private static readonly string[] ProcessedDurationHeaders = { "duration_hr", "duration_hours", "duration" };
    private static readonly string[] ProcessedInterruptionsHeaders = { "interruptions", "interruptions_count", "interruptions_total" };

    private const int DefaultYear = 2025; // overwritten at runtime with DateTime.Now

    public async Task<IReadOnlyList<SleepRecord>> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (!stream.CanSeek)
        {
            using var clone = new MemoryStream();
            await stream.CopyToAsync(clone, cancellationToken);
            clone.Position = 0;
            stream = clone;
        }
        else
        {
            stream.Position = 0;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            BadDataFound = null,
            MissingFieldFound = null,
        });

        if (!await csv.ReadAsync())
        {
            return Array.Empty<SleepRecord>();
        }
        csv.ReadHeader();
        var headers = csv.HeaderRecord?.Select(h => h?.Trim()).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!).ToArray() ?? Array.Empty<string>();
        stream.Position = 0;

        using var reader2 = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var csv2 = new CsvReader(reader2, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            BadDataFound = null,
            MissingFieldFound = null,
        });

        var rawNameHeader = FindHeader(headers, RawNameHeaders);
        var rawDetailsHeader = FindHeader(headers, RawDetailsHeaders);
        if (rawNameHeader is not null && rawDetailsHeader is not null)
        {
            return await ParseRawAsync(csv2, rawNameHeader, rawDetailsHeader, cancellationToken);
        }

        var processedNameHeader = FindHeader(headers, ProcessedNameHeaders) ?? rawNameHeader;
        var startHeader = FindHeader(headers, ProcessedStartHeaders);
        var durationHeader = FindHeader(headers, ProcessedDurationHeaders);
        if (processedNameHeader is not null && startHeader is not null && durationHeader is not null)
        {
            var endHeader = FindHeader(headers, ProcessedEndHeaders);
            var interruptionsHeader = FindHeader(headers, ProcessedInterruptionsHeaders);
            var map = new PreprocessedHeaderMap(processedNameHeader, startHeader, endHeader, durationHeader, interruptionsHeader);
            return await ParsePreprocessedAsync(csv2, map, cancellationToken);
        }

        throw new InvalidOperationException("CSV must contain either columns [Name,Details] or [Name,start_dt,end_dt,duration_hr,interruptions].");
    }

    private static async Task<IReadOnlyList<SleepRecord>> ParsePreprocessedAsync(CsvReader csv, PreprocessedHeaderMap columns, CancellationToken ct)
    {
        if (!await EnsureHeaderReadAsync(csv))
        {
            return Array.Empty<SleepRecord>();
        }

        var records = new List<SleepRecord>();
        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();

            var name = GetField(csv, columns.Name) ?? "Individual";
            var start = GetField(csv, columns.Start);
            var end = columns.End is not null ? GetField(csv, columns.End) : null;
            var duration = GetField(csv, columns.Duration);
            var interruptions = columns.Interruptions is not null ? GetField(csv, columns.Interruptions) : null;

            if (!DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var startDt))
            {
                continue;
            }

            DateTime? endDt = null;
            if (!string.IsNullOrWhiteSpace(end) && DateTime.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedEnd))
            {
                endDt = parsedEnd;
            }

            if (!double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationHr))
            {
                continue;
            }

            double? interruptionsValue = null;
            if (double.TryParse(interruptions, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedInterruptions))
            {
                interruptionsValue = parsedInterruptions;
            }

            records.Add(new SleepRecord
            {
                Name = (name ?? "Individual").Trim(),
                StartDateTime = startDt,
                EndDateTime = endDt,
                DurationHours = durationHr,
                Interruptions = interruptionsValue,
            });
        }

        return FixFutureDatesPerPerson(records);
    }

    private static async Task<IReadOnlyList<SleepRecord>> ParseRawAsync(CsvReader csv, string nameHeader, string detailsHeader, CancellationToken ct)
    {
        if (!await EnsureHeaderReadAsync(csv))
        {
            return Array.Empty<SleepRecord>();
        }

        var rawRows = new List<(string Name, string Details)>();
        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            var name = GetField(csv, nameHeader) ?? "Individual";
            var details = GetField(csv, detailsHeader) ?? string.Empty;
            rawRows.Add((name.Trim(), details));
        }

        if (rawRows.Count == 0)
        {
            return Array.Empty<SleepRecord>();
        }

        var defaultYear = DateTime.Now.Year;
        var records = new List<SleepRecord>();

        foreach (var (name, details) in rawRows)
        {
            var parsed = ParseDetails(details, defaultYear);
            if (parsed is null)
            {
                continue;
            }

            DateTime? startDt = BuildDateTime(parsed.Date, parsed.StartTime, parsed.StartPeriod);
            DateTime? endDt = BuildDateTime(parsed.Date, parsed.EndTime, parsed.EndPeriod);

            if (startDt.HasValue && endDt.HasValue && endDt.Value < startDt.Value)
            {
                endDt = endDt.Value.AddDays(1);
            }

            double? durationMinutes = null;
            if (parsed.Hours.HasValue || parsed.Minutes.HasValue)
            {
                var hours = parsed.Hours ?? 0;
                var minutes = parsed.Minutes ?? 0;
                durationMinutes = hours * 60.0 + minutes;
            }
            else if (startDt.HasValue && endDt.HasValue)
            {
                durationMinutes = (endDt.Value - startDt.Value).TotalMinutes;
            }

            if (!durationMinutes.HasValue || durationMinutes.Value <= 0)
            {
                continue;
            }

            var record = new SleepRecord
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Individual" : name.Trim(),
                StartDateTime = startDt ?? default,
                EndDateTime = endDt,
                DurationHours = durationMinutes.Value / 60.0,
                Interruptions = parsed.Interruptions,
                InterruptionStartMinutes = parsed.InterruptionStarts,
                InterruptionEndMinutes = parsed.InterruptionEnds,
                InterruptionStartMeanMinutes = parsed.InterruptionStartMean,
                InterruptionEndMeanMinutes = parsed.InterruptionEndMean,
            };

            if (record.StartDateTime == default)
            {
                continue;
            }

            records.Add(record);
        }

        return FixFutureDatesPerPerson(records);
    }

    private static string? GetField(CsvReader csv, string header)
    {
        if (csv.TryGetField(header, out string? value))
        {
            return value;
        }

        // CsvHelper allows header matching without exact casing; as a fallback try variant lookups
        foreach (var h in csv.HeaderRecord ?? Array.Empty<string>())
        {
            if (string.Equals(h, header, StringComparison.OrdinalIgnoreCase) && csv.TryGetField(h, out value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FindHeader(IEnumerable<string> headers, IEnumerable<string> candidates) =>
        headers.FirstOrDefault(h => candidates.Any(candidate => string.Equals(h, candidate, StringComparison.OrdinalIgnoreCase)));

    private sealed record PreprocessedHeaderMap(string Name, string Start, string? End, string Duration, string? Interruptions);

    private static async Task<bool> EnsureHeaderReadAsync(CsvReader csv)
    {
        if (csv.Context.Reader.HeaderRecord is not null)
        {
            return true;
        }

        if (!await csv.ReadAsync())
        {
            return false;
        }

        csv.ReadHeader();
        return true;
    }

    // --------------------- Parsing helpers ---------------------
    private sealed record ParsedDetails(
        DateOnly? Date,
        string? StartTime,
        string? StartPeriod,
        string? EndTime,
        string? EndPeriod,
        double? Interruptions,
        double? Hours,
        double? Minutes,
        IReadOnlyList<int>? InterruptionStarts,
        IReadOnlyList<int>? InterruptionEnds,
        int? InterruptionStartMean,
        int? InterruptionEndMean
    );

    private static ParsedDetails? ParseDetails(string raw, int defaultYear)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Replace("\r", string.Empty);

        var dateMatch = DateRegex.Match(normalized);
        var dateValue = ParseDateField(dateMatch.Success ? dateMatch.Groups[1].Value : null, defaultYear);

        var startLine = StartLineRegex.Match(normalized).Value;
        var endLine = EndLineRegex.Match(normalized).Value;

        var (startTime, startPeriod) = ExtractTimeAndPeriod(startLine);
        var (endTime, endPeriod) = ExtractTimeAndPeriod(endLine);

        double? interruptions = null;
        var intrMatch = InterruptionsRegex.Match(normalized);
        if (intrMatch.Success)
        {
            var num = intrMatch.Groups.Cast<Group>().Skip(1).Select(g => g?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (double.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            {
                interruptions = parsedInt;
            }
        }

        double? hours = null;
        var hoursMatch = HoursRegex.Match(normalized);
        if (hoursMatch.Success && double.TryParse(hoursMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
        {
            hours = h;
        }

        double? minutes = null;
        var minutesMatch = MinutesRegex.Match(normalized);
        if (minutesMatch.Success && double.TryParse(minutesMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
        {
            minutes = m;
        }

        var interruptionRegionMatch = InterruptionHeaderRegex.Match(normalized);
        string interruptionRegion = string.Empty;
        if (interruptionRegionMatch.Success)
        {
            interruptionRegion = normalized.Substring(interruptionRegionMatch.Index + interruptionRegionMatch.Length);
        }
        else if (EndLineRegex.Match(normalized) is { Success: true } lastEnd)
        {
            interruptionRegion = normalized[(lastEnd.Index + lastEnd.Length)..];
        }

        var intrStartList = new List<int>();
        var intrEndList = new List<int>();
        if (!string.IsNullOrEmpty(interruptionRegion))
        {
            var startMatches = StartLineRegex.Matches(interruptionRegion);
            var endMatches = EndLineRegex.Matches(interruptionRegion);
            var count = Math.Min(startMatches.Count, endMatches.Count);
            for (var i = 0; i < count; i++)
            {
                var (sTime, sPeriod) = ExtractTimeAndPeriod(startMatches[i].Value);
                var (eTime, ePeriod) = ExtractTimeAndPeriod(endMatches[i].Value);
                var sMinutes = ClockToMinutes(sTime, sPeriod);
                var eMinutes = ClockToMinutes(eTime, ePeriod);
                if (sMinutes.HasValue)
                {
                    intrStartList.Add(sMinutes.Value);
                }
                if (eMinutes.HasValue)
                {
                    intrEndList.Add(eMinutes.Value);
                }
            }
        }

        int? intrStartMean = intrStartList.Count > 0 ? CircularMeanFromMinutesList(intrStartList) : null;
        int? intrEndMean = intrEndList.Count > 0 ? CircularMeanFromMinutesList(intrEndList) : null;

        if (intrStartMean is null && intrEndMean is null)
        {
            // fallback to averages stored in note text if lists absent
            var startMeanMatches = Regex.Matches(normalized, @"intr(?:er)? start mean[:\s]*(\d{1,2}:\d{2})\s*(AM|PM)?", RegexOptions.IgnoreCase);
            if (startMeanMatches.Count > 0)
            {
                var (t, p) = ExtractTimeAndPeriod(startMeanMatches[0].Value);
                intrStartMean = ClockToMinutes(t, p);
            }
            var endMeanMatches = Regex.Matches(normalized, @"intr(?:er)? end mean[:\s]*(\d{1,2}:\d{2})\s*(AM|PM)?", RegexOptions.IgnoreCase);
            if (endMeanMatches.Count > 0)
            {
                var (t, p) = ExtractTimeAndPeriod(endMeanMatches[0].Value);
                intrEndMean = ClockToMinutes(t, p);
            }
        }

        return new ParsedDetails(
            dateValue,
            startTime,
            startPeriod,
            endTime,
            endPeriod,
            interruptions,
            hours,
            minutes,
            intrStartList.Count > 0 ? intrStartList : null,
            intrEndList.Count > 0 ? intrEndList : null,
            intrStartMean,
            intrEndMean
        );
    }

    private static (string? Time, string? Period) ExtractTimeAndPeriod(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return (null, null);
        }

        var time = TimeRegex.Match(line);
        string? timeValue = time.Success ? time.Groups[1].Value : null;

        var checkedPeriod = CheckedPeriodRegex.Match(line);
        if (checkedPeriod.Success)
        {
            var tok = checkedPeriod.Groups[1].Value.ToUpperInvariant().Replace(".", string.Empty);
            var period = tok.StartsWith('A') ? "AM" : tok.StartsWith('P') ? "PM" : null;
            return (timeValue, period);
        }

        var periodMatches = AnyPeriodRegex.Matches(line);
        if (periodMatches.Count > 0)
        {
            var selected = periodMatches[0];
            if (!string.IsNullOrEmpty(timeValue))
            {
                var timeIndex = line.IndexOf(timeValue, StringComparison.Ordinal);
                var after = periodMatches.Cast<Match>().FirstOrDefault(m => m.Index >= timeIndex);
                selected = after ?? selected;
            }

            var tok = selected.Value.ToUpperInvariant().Replace(".", string.Empty);
            var period = tok.StartsWith('A') ? "AM" : tok.StartsWith('P') ? "PM" : null;
            return (timeValue, period);
        }

        return (timeValue, null);
    }

    private static int? ClockToMinutes(string? time, string? period)
    {
        if (string.IsNullOrWhiteSpace(time) || string.IsNullOrWhiteSpace(period))
        {
            return null;
        }

        if (!TimeSpan.TryParseExact(time.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        var (hours, minutes) = ((int)parsed.TotalHours, parsed.Minutes);
        var ap = period.Trim().ToUpperInvariant().Replace(".", string.Empty);

        if (ap.StartsWith('A'))
        {
            if (hours == 12) hours = 0;
        }
        else if (ap.StartsWith('P'))
        {
            if (hours != 12) hours += 12;
        }
        else
        {
            return null;
        }

        return (hours * 60 + minutes) % (24 * 60);
    }

    private static int? CircularMeanFromMinutesList(IEnumerable<int> values)
    {
        var data = values.Select(v => v % (24 * 60)).ToArray();
        if (data.Length == 0)
        {
            return null;
        }

        var angles = data.Select(v => 2 * Math.PI * (v / (24.0 * 60.0))).ToArray();
        var sin = angles.Select(Math.Sin).Average();
        var cos = angles.Select(Math.Cos).Average();
        var angle = Math.Atan2(sin, cos);
        if (angle < 0)
        {
            angle += 2 * Math.PI;
        }

        var minutes = angle * (24.0 * 60.0) / (2 * Math.PI);
        return (int)Math.Round(minutes) % (24 * 60);
    }

    private static DateOnly? ParseDateField(string? raw, int defaultYear)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = DashRegex.Replace(raw.Trim(), "-");
        var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = parts.FirstOrDefault();
        if (first is null)
        {
            return null;
        }

        var match = Regex.Match(first, @"^(?<m>\d{1,2})/(?<d>\d{1,2})(?:/(?<y>\d{2,4}))?$");
        if (!match.Success)
        {
            return null;
        }

        var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);

        int? year = null;
        if (match.Groups["y"].Success)
        {
            year = NormalizeYear(match.Groups["y"].Value);
        }
        else if (parts.Length > 1)
        {
            var second = parts[1];
            var secondary = Regex.Match(second, @"/(?<y>\d{2,4})\s*$");
            if (secondary.Success)
            {
                year = NormalizeYear(secondary.Groups["y"].Value);
            }
        }

        year ??= defaultYear;

        try
        {
            return new DateOnly(year.Value, month, day);
        }
        catch
        {
            return null;
        }
    }

    private static int? NormalizeYear(string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
        {
            return null;
        }

        if (year < 100)
        {
            year += 2000;
        }

        return year;
    }

    private static DateTime? BuildDateTime(DateOnly? date, string? time, string? period)
    {
        if (date is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(time) || string.IsNullOrWhiteSpace(period))
        {
            return null;
        }

        if (!TimeSpan.TryParseExact(time.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        var ap = period.Trim().ToUpperInvariant().Replace(".", string.Empty);
        var hours = parsed.Hours;
        if (ap.StartsWith('A'))
        {
            if (hours == 12) hours = 0;
        }
        else if (ap.StartsWith('P'))
        {
            if (hours != 12) hours += 12;
        }

        var timeOnly = new TimeOnly(hours % 24, parsed.Minutes, parsed.Seconds);
        return date.Value.ToDateTime(timeOnly);
    }

    private static IReadOnlyList<SleepRecord> FixFutureDatesPerPerson(List<SleepRecord> records)
    {
        var today = DateTime.Today;
        var grouped = records.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var output = new List<SleepRecord>(records.Count);

        foreach (var group in grouped)
        {
            foreach (var record in group)
            {
                var start = ToPast(record.StartDateTime, today);
                DateTime? end = record.EndDateTime.HasValue ? ToPast(record.EndDateTime.Value, today) : null;

                if (end.HasValue && end.Value < start)
                {
                    end = end.Value.AddDays(1);
                }

                output.Add(new SleepRecord
                {
                    Name = record.Name,
                    StartDateTime = start,
                    EndDateTime = end,
                    DurationHours = record.DurationHours,
                    Interruptions = record.Interruptions,
                    InterruptionStartMinutes = record.InterruptionStartMinutes,
                    InterruptionEndMinutes = record.InterruptionEndMinutes,
                    InterruptionStartMeanMinutes = record.InterruptionStartMeanMinutes,
                    InterruptionEndMeanMinutes = record.InterruptionEndMeanMinutes,
                });
            }
        }

        return output.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.StartDateTime)
                     .ToList();
    }

    private static DateTime ToPast(DateTime dt, DateTime today)
    {
        var current = dt;
        while (current.Date > today)
        {
            current = SafeReplaceYear(current, current.Year - 1);
        }
        return current;
    }

    private static DateTime SafeReplaceYear(DateTime source, int year)
    {
        try
        {
            return new DateTime(year, source.Month, Math.Min(DateTime.DaysInMonth(year, source.Month), source.Day),
                source.Hour, source.Minute, source.Second, source.Millisecond, source.Kind);
        }
        catch
        {
            if (source.Month == 2 && source.Day == 29)
            {
                return new DateTime(year, 2, 28, source.Hour, source.Minute, source.Second, source.Millisecond, source.Kind);
            }
            return source;
        }
    }
}

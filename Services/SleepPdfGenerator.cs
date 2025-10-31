using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using QuestPDF.Helpers;
using ScottPlot;
using ScottPlot.TickGenerators;
using SleepVisualizationTool.Models;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace SleepVisualizationTool.Services;

/// <summary>
/// Generates per-person PDF reports, mirroring the layout and statistics of the legacy Flask app.
/// </summary>
public sealed class SleepPdfGenerator
{
    private readonly string _storeRoot;

    public SleepPdfGenerator(IWebHostEnvironment environment)
    {
        var basePath = environment.WebRootPath ?? environment.ContentRootPath;
        _storeRoot = Path.Combine(basePath, "generated_pdfs");
        Directory.CreateDirectory(_storeRoot);
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public string EnsureJobDirectory(string jobId)
    {
        var jobDir = Path.Combine(_storeRoot, jobId);
        Directory.CreateDirectory(jobDir);
        return jobDir;
    }

    public async Task<string> GeneratePersonPdfAsync(string jobId, string personName, IReadOnlyList<SleepRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0)
            throw new ArgumentException("At least one record is required.", nameof(records));

        var jobDir = EnsureJobDirectory(jobId);
        var safeName = Sanitize(personName);
        var outputPath = Path.Combine(jobDir, $"{safeName}.pdf");

        var ordered = records.OrderBy(r => r.StartDateTime).ToList();
        var windows = BuildWindowsMostRecentFirst(ordered);
        if (windows.Count == 0)
            throw new InvalidOperationException("No windows available for report.");

        var pages = windows.Select(window =>
        {
            var windowRecords = ordered
                .Where(r => r.StartDateTime.Date >= window.Start.Date && r.StartDateTime.Date <= window.End.Date)
                .OrderBy(r => r.StartDateTime)
                .ToList();

            var stats = ComputeWindowStatistics(windowRecords, window.Start, window.End);
            var chart = RenderChart(windowRecords, stats, personName);
            return new WindowPage(chart, stats);
        })
        .Where(page => page.ChartImage.Length > 0)
        .ToList();

        if (pages.Count == 0)
        {
            throw new InvalidOperationException("No chartable data was produced for the requested report.");
        }

        var doc = new PersonReportDocument(personName, pages);
        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await Task.Run(() => doc.GeneratePdf(stream), ct);
        return outputPath;
    }

    public byte[] BundleJobAsZip(string jobId)
    {
        var jobDir = Path.Combine(_storeRoot, jobId);
        if (!Directory.Exists(jobDir))
            throw new DirectoryNotFoundException(jobDir);

        var files = Directory.EnumerateFiles(jobDir, "*.pdf", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var mem = new MemoryStream();
        using (var archive = new ZipArchive(mem, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.SmallestSize);
                using var entryStream = entry.Open();
                using var source = File.OpenRead(file);
                source.CopyTo(entryStream);
            }
        }

        mem.Position = 0;
        return mem.ToArray();
    }

    public IEnumerable<string> EnumerateJobPdfs(string jobId)
    {
        var jobDir = Path.Combine(_storeRoot, jobId);
        if (!Directory.Exists(jobDir))
            return Enumerable.Empty<string>();

        return Directory.EnumerateFiles(jobDir, "*.pdf", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    public string GetJobFilePath(string jobId, string filename)
    {
        var jobDir = Path.Combine(_storeRoot, jobId);
        var full = Path.GetFullPath(Path.Combine(jobDir, filename));
        if (!full.StartsWith(Path.GetFullPath(jobDir), StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid file path.");
        return full;
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Individual";

        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray();
        var cleaned = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(cleaned) ? "Individual" : cleaned;
    }

    private static List<(DateTime Start, DateTime End)> BuildWindowsMostRecentFirst(IReadOnlyList<SleepRecord> personRecords)
    {
        var dates = personRecords.Select(r => r.StartDateTime.Date).OrderBy(d => d).ToList();
        if (dates.Count == 0)
            return [];

        var personMin = dates.First();
        var personMax = dates.Last();

        var windows = new List<(DateTime, DateTime)>();
        var axisEnd = personMax;
        while (axisEnd >= personMin)
        {
            var axisStart = axisEnd.AddMonths(-6).AddDays(1);
            if (axisStart < personMin)
                axisStart = personMin;
            windows.Add((axisStart, axisEnd));
            axisEnd = axisStart.AddDays(-1);
        }

        return windows;
    }

    private static WindowStatistics ComputeWindowStatistics(IReadOnlyList<SleepRecord> records, DateTime windowStart, DateTime windowEnd)
    {
        if (records.Count == 0)
        {
            return new WindowStatistics
            {
                WindowStart = windowStart,
                WindowEnd = windowEnd,
            };
        }

        var durations = records.Select(r => r.DurationHours).Where(d => !double.IsNaN(d) && !double.IsInfinity(d)).ToArray();
        var avgDuration = durations.Length > 0 ? durations.Average() : (double?)null;

        var interruptionNumbers = records
            .Select(r => r.Interruptions)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();
        var avgInterruptions = interruptionNumbers.Length > 0 ? interruptionNumbers.Average() : (double?)null;

        var startMinutes = records.Select(r => (int?)((r.StartDateTime.Hour * 60) + r.StartDateTime.Minute)).ToList();
        var startMean = CircularMean(startMinutes);
        var avgMinutes = avgDuration.HasValue ? (int?)Math.Round(avgDuration.Value * 60.0) : null;
        var endMean = AddMinutesCircular(startMean, avgMinutes);

        var intrStartCollection = new List<int>();
        var intrEndCollection = new List<int>();

        foreach (var record in records)
        {
            if (record.InterruptionStartMinutes is { } sList)
            {
                intrStartCollection.AddRange(sList);
            }
            if (record.InterruptionEndMinutes is { } eList)
            {
                intrEndCollection.AddRange(eList);
            }
        }

        int? intrStartMean = intrStartCollection.Count > 0
            ? CircularMean(intrStartCollection.Select(i => (int?)i).ToList())
            : records.Select(r => r.InterruptionStartMeanMinutes).FirstOrDefault(v => v.HasValue);

        int? intrEndMean = intrEndCollection.Count > 0
            ? CircularMean(intrEndCollection.Select(i => (int?)i).ToList())
            : records.Select(r => r.InterruptionEndMeanMinutes).FirstOrDefault(v => v.HasValue);

        return new WindowStatistics
        {
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            AverageDurationHours = avgDuration,
            AverageInterruptions = avgInterruptions,
            AverageStartMinutes = startMean,
            AverageEndMinutes = endMean,
            InterruptionStartMean = intrStartMean,
            InterruptionEndMean = intrEndMean,
        };
    }

    private static byte[] RenderChart(IReadOnlyList<SleepRecord> records, WindowStatistics stats, string personName)
    {
        if (records.Count == 0)
            return Array.Empty<byte>();

        var xDates = records.Select(r => r.StartDateTime).ToArray();
        var xs = xDates.Select(d => d.ToOADate()).ToArray();
        var ys = records.Select(r => r.DurationHours).ToArray();

        var plot = new Plot();
        plot.Axes.Title.Label.Text = $"Sleep duration — {personName}";
        plot.Axes.Left.Label.Text = "Duration (hours)";
        plot.Axes.Bottom.Label.Text = string.Empty;

        plot.Axes.Bottom.TickGenerator = new DateTimeAutomatic();

        var scatter = plot.Add.Scatter(xs, ys, color: ScottPlot.Color.FromHex("#2196F3"));
        scatter.MarkerSize = 6;

        var obsMin = xDates.Min().Date;
        var obsMax = xDates.Max().Date;
        if (obsMin == obsMax)
        {
            obsMin = obsMin.AddDays(-3);
            obsMax = obsMax.AddDays(3);
        }

        plot.Axes.SetLimits(left: obsMin.ToOADate(), right: obsMax.ToOADate(), bottom: null, top: null);

        if (stats.AverageDurationHours is { } avgDur)
        {
            var avgLine = plot.Add.HorizontalLine(avgDur);
            avgLine.Color = ScottPlot.Colors.Black;
            avgLine.LinePattern = ScottPlot.LinePattern.Dotted;
        }

        if (records.Count >= 2 && ys.Distinct().Count() > 1)
        {
            var regression = new ScottPlot.Statistics.LinearRegression(xs, ys);
            var xFit = new[] { xs.Min(), xs.Max() };
            var yFit = regression.GetValues(xFit);
            var trend = plot.Add.Scatter(xFit, yFit, color: ScottPlot.Color.FromHex("#F44336"));
            trend.LineStyle.Pattern = ScottPlot.LinePattern.Dashed;
            trend.MarkerSize = 0;
        }

        var sb = new StringBuilder();
        sb.AppendLine(FormatStatsLine("Avg sleep", stats.AverageDurationHours));
        sb.AppendLine(FormatClockLine("Avg start", stats.AverageStartMinutes));
        sb.AppendLine(FormatClockLine("Avg end", stats.AverageEndMinutes));
        sb.AppendLine(FormatStatsLine("Avg interruptions", stats.AverageInterruptions));
        sb.AppendLine(FormatClockLine("Avg intr. start", stats.InterruptionStartMean));
        sb.AppendLine(FormatClockLine("Avg intr. end", stats.InterruptionEndMean));

        var annotation = plot.Add.Text(sb.ToString(), obsMax.ToOADate(), ys.Max());
        annotation.Color = ScottPlot.Colors.Black;
        annotation.Size = 12;

        return plot.GetImageBytes(1000, 500, ScottPlot.ImageFormat.Png);
    }

    private static string FormatStatsLine(string label, double? value)
    {
        if (!value.HasValue)
            return $"{label}: NA";

        var hours = (int)Math.Truncate(value.Value);
        var minutes = (int)Math.Round((value.Value - hours) * 60) % 60;
        return $"{label}: {value.Value:F2} h ({hours}h {minutes:00}m)";
    }

    private static string FormatClockLine(string label, int? minutes)
    {
        if (!minutes.HasValue)
            return $"{label}: NA";

        var mins = (minutes.Value % (24 * 60) + (24 * 60)) % (24 * 60);
        var hours = mins / 60;
        var minsPart = mins % 60;
        var suffix = hours < 12 ? "AM" : "PM";
        var hours12 = hours % 12 == 0 ? 12 : hours % 12;
        return $"{label}: {hours12}:{minsPart:00} {suffix}";
    }

    private static int? CircularMean(IReadOnlyList<int?> values)
    {
        var filtered = values.Where(v => v.HasValue).Select(v => v!.Value % (24 * 60)).ToArray();
        if (filtered.Length == 0)
            return null;

        var angles = filtered.Select(v => 2 * Math.PI * (v / (24.0 * 60.0))).ToArray();
        var sin = angles.Select(Math.Sin).Average();
        var cos = angles.Select(Math.Cos).Average();
        var angle = Math.Atan2(sin, cos);
        if (angle < 0)
            angle += 2 * Math.PI;
        var minutes = angle * (24.0 * 60.0) / (2 * Math.PI);
        return (int)Math.Round(minutes) % (24 * 60);
    }

    private static int? AddMinutesCircular(int? minsFromMidnight, int? deltaMinutes)
    {
        if (!minsFromMidnight.HasValue || !deltaMinutes.HasValue)
            return null;

        var value = (minsFromMidnight.Value + deltaMinutes.Value) % (24 * 60);
        return (value + (24 * 60)) % (24 * 60);
    }

    // ------------ QuestPDF Documents ------------
    private sealed record WindowPage(byte[] ChartImage, WindowStatistics Stats);

    private sealed class PersonReportDocument : IDocument
    {
        private readonly string _personName;
        private readonly IReadOnlyList<WindowPage> _pages;

        public PersonReportDocument(string personName, IReadOnlyList<WindowPage> pages)
        {
            _personName = personName;
            _pages = pages;
        }

        public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

        public void Compose(IDocumentContainer container)
        {
            container.Page(page =>
            {
                page.Margin(30);
                page.Size(PageSizes.A4);

                page.Header().Column(column =>
                {
                    column.Spacing(5);
                    column.Item().Text("Sleep Charts").FontSize(20).Bold();
                    column.Item().Text(_personName).FontSize(15);
                });

                page.Content().Column(column =>
                {
                    for (var i = 0; i < _pages.Count; i++)
                    {
                        var window = _pages[i];
                        column.Item().Element(_ => ComposeWindow(_, window));
                        if (i < _pages.Count - 1)
                        {
                            column.Item().PageBreak();
                        }
                    }
                });
            });
        }

        private void ComposeWindow(IContainer container, WindowPage window)
        {
            var stats = window.Stats;
            container.Column(col =>
            {
                col.Spacing(10);
                col.Item().Text($"{stats.WindowStart:MMM d, yyyy} – {stats.WindowEnd:MMM d, yyyy}").FontSize(12).SemiBold();
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(statCol =>
                    {
                        statCol.Spacing(4);
                        statCol.Item().Text(FormatStatsLine("Avg sleep", stats.AverageDurationHours));
                        statCol.Item().Text(FormatClockLine("Avg start", stats.AverageStartMinutes));
                        statCol.Item().Text(FormatClockLine("Avg end", stats.AverageEndMinutes));
                        statCol.Item().Text(FormatStatsLine("Avg interruptions", stats.AverageInterruptions));
                        statCol.Item().Text(FormatClockLine("Avg intr. start", stats.InterruptionStartMean));
                        statCol.Item().Text(FormatClockLine("Avg intr. end", stats.InterruptionEndMean));
                    });
                });

                if (window.ChartImage.Length > 0)
                {
                    col.Item().Image(window.ChartImage);
                }
                else
                {
                    col.Item().Text("No data available for this window.").Italic();
                }
            });
        }
    }
}

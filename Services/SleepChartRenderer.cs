using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ScottPlot;
using ScottPlot.TickGenerators;
using SleepVisualizationTool.Models;

namespace SleepVisualizationTool.Services;

public sealed class SleepChartRenderer
{
    public IReadOnlyList<SleepChartWindow> BuildWindows(string personName, IReadOnlyList<SleepRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return Array.Empty<SleepChartWindow>();
        }

        var ordered = records.OrderBy(r => r.StartDateTime).ToList();
        var ranges = BuildWindowsMostRecentFirst(ordered);
        var windows = new List<SleepChartWindow>();

        foreach (var (start, end) in ranges)
        {
            var windowRecords = ordered
                .Where(r => r.StartDateTime.Date >= start.Date && r.StartDateTime.Date <= end.Date)
                .OrderBy(r => r.StartDateTime)
                .ToList();

            var stats = ComputeWindowStatistics(windowRecords, start, end);
            var summaryLines = BuildSummaryLines(stats);
            var annotation = string.Join(Environment.NewLine, summaryLines);
            var chart = RenderChart(windowRecords, stats, personName, annotation);

            if (chart.Length > 0)
            {
                windows.Add(new SleepChartWindow(start, end, stats, summaryLines, annotation, chart));
            }
        }

        return windows;
    }

    public SleepChartOverview RenderOverview(string personName, IReadOnlyList<SleepRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            throw new ArgumentException("At least one record is required.", nameof(records));
        }

        var ordered = records.OrderBy(r => r.StartDateTime).ToList();
        var start = ordered.First().StartDateTime.Date;
        var end = ordered.Last().StartDateTime.Date;

        var stats = ComputeWindowStatistics(ordered, start, end);
        var summaryLines = BuildSummaryLines(stats);
        var annotation = string.Join(Environment.NewLine, summaryLines);
        var chart = RenderChart(ordered, stats, personName, annotation);

        return new SleepChartOverview(personName, start, end, stats, summaryLines, annotation, chart);
    }

    private static List<(DateTime Start, DateTime End)> BuildWindowsMostRecentFirst(IReadOnlyList<SleepRecord> personRecords)
    {
        var dates = personRecords.Select(r => r.StartDateTime.Date).OrderBy(d => d).ToList();
        if (dates.Count == 0)
            return new();

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

    private static IReadOnlyList<string> BuildSummaryLines(WindowStatistics stats)
    {
        return new[]
        {
            FormatStatsLine("Avg sleep", stats.AverageDurationHours),
            FormatClockLine("Avg start", stats.AverageStartMinutes),
            FormatClockLine("Avg end", stats.AverageEndMinutes),
            FormatStatsLine("Avg interruptions", stats.AverageInterruptions),
            FormatClockLine("Avg intr. start", stats.InterruptionStartMean),
            FormatClockLine("Avg intr. end", stats.InterruptionEndMean)
        };
    }

    private static byte[] RenderChart(IReadOnlyList<SleepRecord> records, WindowStatistics stats, string personName, string annotationText)
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

        var scatter = plot.Add.Scatter(xs, ys, color: Color.FromHex("#2196F3"));
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
            avgLine.Color = Colors.Black;
            avgLine.LinePattern = LinePattern.Dotted;
        }

        if (records.Count >= 2 && ys.Distinct().Count() > 1)
        {
            var regression = new ScottPlot.Statistics.LinearRegression(xs, ys);
            var xFit = new[] { xs.Min(), xs.Max() };
            var yFit = regression.GetValues(xFit);
            var trend = plot.Add.Scatter(xFit, yFit, color: Color.FromHex("#F44336"));
            trend.LineStyle.Pattern = LinePattern.Dashed;
            trend.MarkerSize = 0;
        }

        if (!string.IsNullOrWhiteSpace(annotationText))
        {
            var annotation = plot.Add.Text(annotationText, obsMax.ToOADate(), ys.Max());
            annotation.Color = Colors.Black;
            annotation.Size = 12;
        }

        return plot.GetImageBytes(1000, 500, ImageFormat.Png);
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
}

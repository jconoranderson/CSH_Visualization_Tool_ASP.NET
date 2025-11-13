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
        ranges.Reverse();
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

        var interruptionDurations = records
            .SelectMany(r => r.GetInterruptionDurationsHours())
            .Where(d => !double.IsNaN(d) && !double.IsInfinity(d) && d > 0)
            .ToArray();
        var avgInterruptionLength = interruptionDurations.Length > 0 ? interruptionDurations.Average() : (double?)null;

        var interruptionCounts = records
            .Select(r => r.GetInterruptionCount())
            .Where(v => v.HasValue && v.Value > 0)
            .Select(v => v!.Value)
            .ToArray();
        var avgInterruptionCount = interruptionCounts.Length > 0 ? interruptionCounts.Average() : (double?)null;

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
            AverageInterruptions = avgInterruptionLength,
            AverageInterruptionCount = avgInterruptionCount,
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
            FormatStatsLine("Avg intr. length", stats.AverageInterruptions),
            FormatCountLine("Avg intr. total", stats.AverageInterruptionCount),
            FormatClockLine("Avg intr. start", stats.InterruptionStartMean)
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

        var baseLine = plot.Add.Scatter(xs, ys, color: Color.FromHex("#64B5F6"));
        baseLine.MarkerSize = 0;

        var hadInterruptionColor = Color.FromHex("#1E88E5");
        var noInterruptionColor = Color.FromHex("#FB8C00");
        var noInterruptionXs = new List<double>();
        var noInterruptionYs = new List<double>();
        var interruptionXs = new List<double>();
        var interruptionYs = new List<double>();

        for (var i = 0; i < records.Count; i++)
        {
            var hasInterruptions = records[i].HasInterruptionEvidence();
            if (hasInterruptions)
            {
                interruptionXs.Add(xs[i]);
                interruptionYs.Add(ys[i]);
            }
            else
            {
                noInterruptionXs.Add(xs[i]);
                noInterruptionYs.Add(ys[i]);
            }
        }

        var hasNoInterruptionPoints = noInterruptionXs.Count > 0;
        var hasInterruptionPoints = interruptionXs.Count > 0;

        if (hasNoInterruptionPoints)
        {
            var cleanScatter = plot.Add.Scatter(noInterruptionXs, noInterruptionYs, color: noInterruptionColor);
            cleanScatter.LineStyle.Width = 0;
            cleanScatter.MarkerSize = 7;
            cleanScatter.Label = "No Interruptions";
        }

        if (hasInterruptionPoints)
        {
            var intrScatter = plot.Add.Scatter(interruptionXs, interruptionYs, color: hadInterruptionColor);
            intrScatter.LineStyle.Width = 0;
            intrScatter.MarkerSize = 7;
            intrScatter.Label = "Had Interruptions";
        }

        var obsMin = xDates.Min().Date;
        var obsMax = xDates.Max().Date;
        if (obsMin == obsMax)
        {
            obsMin = obsMin.AddDays(-3);
            obsMax = obsMax.AddDays(3);
        }

        var spanDays = Math.Max((obsMax - obsMin).TotalDays, 1);
        var paddingDays = Math.Max(spanDays * 0.04, 0.5);
        obsMin = obsMin.AddDays(-paddingDays);
        obsMax = obsMax.AddDays(paddingDays);

        plot.Axes.SetLimits(left: obsMin.ToOADate(), right: obsMax.ToOADate(), bottom: 0, top: 15);

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

        if (hasInterruptionPoints || hasNoInterruptionPoints)
        {
            AddLegendOverlay(plot, obsMin, obsMax, hasInterruptionPoints, hasNoInterruptionPoints, hadInterruptionColor, noInterruptionColor);
        }

        return plot.GetImageBytes(1000, 500, ImageFormat.Png);
    }

    private static void AddLegendOverlay(Plot plot, DateTime obsMin, DateTime obsMax, bool showInterruptions, bool showNoInterruptions, Color interruptionColor, Color noInterruptionColor)
    {
        var entries = new List<(string Label, Color Color)>
        {
            ("Had interruptions (blue)", interruptionColor),
            ("No interruptions (orange)", noInterruptionColor)
        };

        if (!showInterruptions)
            entries.RemoveAt(0);
        if (!showNoInterruptions)
            entries.RemoveAt(entries.Count - 1);

        if (entries.Count == 0)
            return;

        var spanDays = Math.Max((obsMax - obsMin).TotalDays, 1);
        var startX = obsMin.AddDays(Math.Max(spanDays * 0.02, 0.5)).ToOADate();
        var textOffsetDays = Math.Max(spanDays * 0.015, 0.2);
        var rowHeight = 0.8;
        var topY = 14.2;

        var backgroundWidthDays = Math.Max(spanDays * 0.25, 5);
        var backgroundHeight = rowHeight * entries.Count + 0.8;

        var xMin = startX - Math.Max(spanDays * 0.01, 0.2);
        var xMax = startX + backgroundWidthDays;
        var yMax = topY + 0.6;
        var yMin = topY - backgroundHeight;

        var legendBackground = plot.Add.Rectangle(xMin, xMax, yMin, yMax);
        legendBackground.FillStyle.Color = Colors.White.WithOpacity(0.85);
        legendBackground.LineStyle.Color = Colors.Black.WithOpacity(0.15);
        legendBackground.LineStyle.Width = 1;

        var title = plot.Add.Text("Legend", xMin + 0.2, yMax - 0.2);
        title.Color = Colors.Black;
        title.Size = 14;

        var currentY = topY - 0.3;
        foreach (var (label, color) in entries)
        {
            plot.Add.Marker(startX, currentY, MarkerShape.FilledCircle, 12, color);

            var text = plot.Add.Text(label, startX + textOffsetDays, currentY);
            text.Color = Colors.Black;
            text.Size = 12;

            currentY -= rowHeight;
        }
    }

    private static string FormatStatsLine(string label, double? value)
    {
        if (!value.HasValue)
            return $"{label}: NA";

        var totalMinutes = (int)Math.Round(value.Value * 60);
        var hours = totalMinutes / 60;
        var minutes = Math.Abs(totalMinutes % 60);
        return $"{label}: {hours}h {minutes:00}m";
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

    private static string FormatCountLine(string label, double? value)
    {
        if (!value.HasValue)
            return $"{label}: NA";

        return $"{label}: {value.Value:F1}";
    }
}

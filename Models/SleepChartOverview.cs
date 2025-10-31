using System;
using System.Collections.Generic;

namespace SleepVisualizationTool.Models;

public sealed record SleepChartOverview(
    string PersonName,
    DateTime WindowStart,
    DateTime WindowEnd,
    WindowStatistics Statistics,
    IReadOnlyList<string> SummaryLines,
    string AnnotationText,
    byte[] ChartImage)
{
    public bool HasChart => ChartImage.Length > 0;
    public string ChartDataUrl => $"data:image/png;base64,{Convert.ToBase64String(ChartImage)}";
    public string WindowLabel => $"{WindowStart:MMM d, yyyy} – {WindowEnd:MMM d, yyyy}";
}

public sealed record SleepChartWindow(
    DateTime WindowStart,
    DateTime WindowEnd,
    WindowStatistics Statistics,
    IReadOnlyList<string> SummaryLines,
    string AnnotationText,
    byte[] ChartImage)
{
    public bool HasChart => ChartImage.Length > 0;
    public string WindowLabel => $"{WindowStart:MMM d, yyyy} – {WindowEnd:MMM d, yyyy}";
}

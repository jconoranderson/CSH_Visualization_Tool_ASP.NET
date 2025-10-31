using System.Collections.Generic;

namespace SleepVisualizationTool.Models;

public sealed class PersonVisualizationViewModel
{
    public required string Name { get; init; }
    public required IReadOnlyList<PersonVisualizationWindow> Windows { get; init; }
    public bool HasMultipleWindows => Windows.Count > 1;
}

public sealed class PersonVisualizationWindow
{
    public required int Index { get; init; }
    public required string Label { get; init; }
    public required string ChartDataUrl { get; init; }
    public required IReadOnlyList<string> SummaryLines { get; init; }
}

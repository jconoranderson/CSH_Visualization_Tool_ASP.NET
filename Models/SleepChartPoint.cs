using System;

namespace SleepVisualizationTool.Models;

public sealed class SleepChartPoint
{
    public required DateTime StartDateTime { get; init; }
    public DateTime? EndDateTime { get; init; }
    public required double DurationHours { get; init; }
    public double? InterruptionCount { get; init; }
    public double? AverageInterruptionLengthHours { get; init; }
    public bool HasInterruptions { get; init; }
}

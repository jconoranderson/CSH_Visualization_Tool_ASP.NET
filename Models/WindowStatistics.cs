namespace SleepVisualizationTool.Models;

public sealed class WindowStatistics
{
    public DateTime WindowStart { get; init; }
    public DateTime WindowEnd { get; init; }
    public double? AverageDurationHours { get; init; }
    public double? AverageInterruptions { get; init; }
    public int? AverageStartMinutes { get; init; }
    public int? AverageEndMinutes { get; init; }
    public int? InterruptionStartMean { get; init; }
    public int? InterruptionEndMean { get; init; }
}

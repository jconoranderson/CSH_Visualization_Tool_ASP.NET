using System.Globalization;

namespace SleepVisualizationTool.Models;

/// <summary>
/// Normalised sleep record for a single row in the uploaded CSV.
/// Mirrors the structure produced by the original Flask application.
/// </summary>
public sealed class SleepRecord
{
    public string Name { get; set; } = "Individual";

    /// <summary>
    /// Start of the sleep interval (normalized, future dates pushed into the past).
    /// </summary>
    public DateTime StartDateTime { get; set; }

    /// <summary>
    /// End of the sleep interval (may be null when not supplied).
    /// </summary>
    public DateTime? EndDateTime { get; set; }

    /// <summary>
    /// Duration in hours (fan-in of explicit Hours/Minutes fields or derived from timestamps).
    /// </summary>
    public double DurationHours { get; set; }

    public double? Interruptions { get; set; }

    /// <summary>
    /// Raw list of interruption start minutes from midnight (if present).
    /// </summary>
    public IReadOnlyList<int>? InterruptionStartMinutes { get; set; }

    /// <summary>
    /// Raw list of interruption end minutes from midnight (if present).
    /// </summary>
    public IReadOnlyList<int>? InterruptionEndMinutes { get; set; }

    public int? InterruptionStartMeanMinutes { get; set; }
    public int? InterruptionEndMeanMinutes { get; set; }

    /// <summary>
    /// Convenience accessor for the start date only (used for windowing).
    /// </summary>
    public DateOnly StartDate => DateOnly.FromDateTime(StartDateTime);

    /// <summary>
    /// Formats the record for diagnostics.
    /// </summary>
    public override string ToString()
        => FormattableString.Invariant($"{Name}: {StartDateTime.ToString("g", CultureInfo.InvariantCulture)} ({DurationHours:F2}h)");
}

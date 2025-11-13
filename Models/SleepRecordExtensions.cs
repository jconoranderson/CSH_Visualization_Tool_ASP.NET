using System;
using System.Collections.Generic;
using System.Linq;

namespace SleepVisualizationTool.Models;

public static class SleepRecordExtensions
{
    public static IEnumerable<double> GetInterruptionDurationsHours(this SleepRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Interruptions is { } interruptionCount && interruptionCount <= 0)
        {
            yield break;
        }

        if (record.InterruptionStartMinutes is not { Count: > 0 } starts ||
            record.InterruptionEndMinutes is not { Count: > 0 } ends)
        {
            yield break;
        }

        var pairs = Math.Min(starts.Count, ends.Count);
        for (var i = 0; i < pairs; i++)
        {
            var start = starts[i];
            var end = ends[i];
            if (end < start)
            {
                end += 24 * 60;
            }

            var durationMinutes = end - start;
            if (durationMinutes > 0)
            {
                yield return durationMinutes / 60.0;
            }
        }
    }

    public static double? AverageInterruptionLengthHours(this SleepRecord record)
    {
        var durations = record.GetInterruptionDurationsHours().ToArray();
        if (durations.Length == 0)
        {
            return null;
        }

        return durations.Average();
    }

    public static bool HasInterruptionEvidence(this SleepRecord record)
    {
        if (record.Interruptions.HasValue && record.Interruptions.Value > 0)
        {
            return true;
        }

        if (record.InterruptionStartMinutes is { Count: > 0 } ||
            record.InterruptionEndMinutes is { Count: > 0 })
        {
            return true;
        }

        return false;
    }

    public static double? GetInterruptionCount(this SleepRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Interruptions is { } explicitCount && explicitCount > 0)
        {
            return explicitCount;
        }

        var derived = Math.Max(record.InterruptionStartMinutes?.Count ?? 0, record.InterruptionEndMinutes?.Count ?? 0);
        return derived > 0 ? derived : null;
    }

    public static DateTime? GetSleepEnd(this SleepRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.EndDateTime is { } explicitEnd)
        {
            return explicitEnd;
        }

        if (record.DurationHours > 0)
        {
            return record.StartDateTime.AddMinutes(record.DurationHours * 60.0);
        }

        return null;
    }
}

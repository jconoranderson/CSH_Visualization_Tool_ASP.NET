namespace SleepVisualizationTool.Models;

public sealed class UploadResultViewModel
{
    public required string JobId { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public int Count => Files.Count;
}

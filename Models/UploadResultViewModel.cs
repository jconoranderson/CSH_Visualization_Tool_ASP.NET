namespace SleepVisualizationTool.Models;

public sealed class UploadResultViewModel
{
    public required string JobId { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required IReadOnlyList<PersonVisualizationViewModel> Visualizations { get; init; }
    public int Count => Files.Count;
    public bool HasVisualizations => Visualizations.Count > 0;
}

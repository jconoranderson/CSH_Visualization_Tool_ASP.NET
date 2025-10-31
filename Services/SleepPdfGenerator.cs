using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using QuestPDF.Helpers;
using SleepVisualizationTool.Models;
using System.IO.Compression;

namespace SleepVisualizationTool.Services;

/// <summary>
/// Generates per-person PDF reports, mirroring the layout and statistics of the legacy Flask app.
/// </summary>
public sealed class SleepPdfGenerator
{
    private readonly string _storeRoot;
    private readonly SleepChartRenderer _chartRenderer;

    public SleepPdfGenerator(IWebHostEnvironment environment, SleepChartRenderer chartRenderer)
    {
        _chartRenderer = chartRenderer;
        var basePath = Path.Combine(Path.GetTempPath(), "SleepVisualizationTool");
        _storeRoot = Path.Combine(basePath, "generated_pdfs");
        Directory.CreateDirectory(_storeRoot);
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public string EnsureJobDirectory(string jobId)
    {
        var jobDir = Path.Combine(_storeRoot, jobId);
        Directory.CreateDirectory(jobDir);
        return jobDir;
    }

    public async Task<string> GeneratePersonPdfAsync(string jobId, string personName, IReadOnlyList<SleepRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0)
            throw new ArgumentException("At least one record is required.", nameof(records));

        var jobDir = EnsureJobDirectory(jobId);
       var safeName = Sanitize(personName);
        var outputPath = Path.Combine(jobDir, $"{safeName}.pdf");

        var ordered = records.OrderBy(r => r.StartDateTime).ToList();
        var windows = _chartRenderer.BuildWindows(personName, ordered)
            .Where(w => w.HasChart)
            .ToList();

        if (windows.Count == 0)
        {
            throw new InvalidOperationException("No chartable data was produced for the requested report.");
        }

        var doc = new PersonReportDocument(personName, windows);
        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await Task.Run(() => doc.GeneratePdf(stream), ct);
        return outputPath;
    }

    public byte[] BundleJobAsZip(string jobId)
    {
        var jobDir = Path.Combine(_storeRoot, jobId);
        if (!Directory.Exists(jobDir))
            throw new DirectoryNotFoundException(jobDir);

        var files = Directory.EnumerateFiles(jobDir, "*.pdf", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var mem = new MemoryStream();
        using (var archive = new ZipArchive(mem, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.SmallestSize);
                using var entryStream = entry.Open();
                using var source = File.OpenRead(file);
                source.CopyTo(entryStream);
            }
        }

        mem.Position = 0;
        return mem.ToArray();
    }

    public IEnumerable<string> EnumerateJobPdfs(string jobId)
    {
        var jobDir = Path.Combine(_storeRoot, jobId);
        if (!Directory.Exists(jobDir))
            return Enumerable.Empty<string>();

        return Directory.EnumerateFiles(jobDir, "*.pdf", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    public string GetJobFilePath(string jobId, string filename)
    {
        var jobDir = Path.Combine(_storeRoot, jobId);
        var full = Path.GetFullPath(Path.Combine(jobDir, filename));
        if (!full.StartsWith(Path.GetFullPath(jobDir), StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid file path.");
        return full;
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Individual";

        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray();
        var cleaned = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(cleaned) ? "Individual" : cleaned;
    }

    // ------------ QuestPDF Documents ------------
    private sealed class PersonReportDocument : IDocument
    {
        private readonly string _personName;
        private readonly IReadOnlyList<SleepChartWindow> _pages;

        public PersonReportDocument(string personName, IReadOnlyList<SleepChartWindow> pages)
        {
            _personName = personName;
            _pages = pages;
        }

        public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

        public void Compose(IDocumentContainer container)
        {
            container.Page(page =>
            {
                page.Margin(30);
                page.Size(PageSizes.A4);

                page.Header().Column(column =>
                {
                    column.Spacing(5);
                    column.Item().Text("Sleep Charts").FontSize(20).Bold();
                    column.Item().Text(_personName).FontSize(15);
                });

                page.Content().Column(column =>
                {
                    for (var i = 0; i < _pages.Count; i++)
                    {
                        var window = _pages[i];
                        column.Item().Element(_ => ComposeWindow(_, window));
                        if (i < _pages.Count - 1)
                        {
                            column.Item().PageBreak();
                        }
                    }
                });
            });
        }

        private void ComposeWindow(IContainer container, SleepChartWindow window)
        {
            var stats = window.Statistics;
            container.Column(col =>
            {
                col.Spacing(10);
                col.Item().Text(window.WindowLabel).FontSize(12).SemiBold();
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(statCol =>
                    {
                        statCol.Spacing(4);
                        foreach (var line in window.SummaryLines)
                        {
                            statCol.Item().Text(line);
                        }
                    });
                });

                if (window.HasChart)
                {
                    col.Item().Image(window.ChartImage);
                }
                else
                {
                    col.Item().Text("No data available for this window.").Italic();
                }
            });
        }
    }
}

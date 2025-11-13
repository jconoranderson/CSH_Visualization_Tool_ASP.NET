using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SleepVisualizationTool.Models;
using SleepVisualizationTool.Services;

namespace SleepVisualizationTool.Controllers;

public sealed class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly SleepCsvLoader _csvLoader;
    private readonly SleepPdfGenerator _pdfGenerator;
    private readonly SleepChartRenderer _chartRenderer;

    public HomeController(
        ILogger<HomeController> logger,
        SleepCsvLoader csvLoader,
        SleepPdfGenerator pdfGenerator,
        SleepChartRenderer chartRenderer)
    {
        _logger = logger;
        _csvLoader = csvLoader;
        _pdfGenerator = pdfGenerator;
        _chartRenderer = chartRenderer;
    }

    [HttpGet]
    public IActionResult Index() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(IFormFile? csv, CancellationToken cancellationToken)
    {
        if (csv is null || csv.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Please choose a CSV file to upload.");
            return View("Index");
        }

        if (!csv.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(string.Empty, "Only .csv files are supported.");
            return View("Index");
        }

        IReadOnlyList<SleepRecord> records;
        try
        {
            await using var stream = csv.OpenReadStream();
            records = await _csvLoader.LoadAsync(stream, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ModelState.AddModelError(string.Empty, "Upload was canceled before it could complete.");
            return View("Index");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse uploaded CSV.");
            ModelState.AddModelError(string.Empty, $"Failed to parse CSV: {ex.Message}");
            return View("Index");
        }

        if (records.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "No valid sleep records were found in the upload.");
            return View("Index");
        }

        var groups = records
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groups.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "No per-person records could be generated.");
            return View("Index");
        }

        var jobId = Guid.NewGuid().ToString("N");
        var fileNames = new List<string>();
        var visualizations = new List<PersonVisualizationViewModel>();

        foreach (var group in groups)
        {
            var personRecords = group.ToList();

            try
            {
                var overview = _chartRenderer.RenderOverview(group.Key, personRecords);
                var windowSegments = _chartRenderer.BuildWindows(group.Key, personRecords);

                var windowViewModels = new List<PersonVisualizationWindow>();
                if (overview.HasChart)
                {
                    var overviewPoints = BuildPoints(personRecords);
                    windowViewModels.Add(new PersonVisualizationWindow
                    {
                        Index = 0,
                        Label = $"All history ({overview.WindowLabel})",
                        ChartDataUrl = overview.ChartDataUrl,
                        SummaryLines = overview.SummaryLines,
                        WindowStart = overview.WindowStart,
                        WindowEnd = overview.WindowEnd,
                        Points = overviewPoints,
                        Statistics = overview.Statistics
                    });
                }

                var nextIndex = windowViewModels.Count;
                foreach (var segment in windowSegments)
                {
                    var windowPoints = BuildPoints(personRecords, segment.WindowStart, segment.WindowEnd);
                    windowViewModels.Add(new PersonVisualizationWindow
                    {
                        Index = nextIndex++,
                        Label = segment.WindowLabel,
                        ChartDataUrl = $"data:image/png;base64,{Convert.ToBase64String(segment.ChartImage)}",
                        SummaryLines = segment.SummaryLines,
                        WindowStart = segment.WindowStart,
                        WindowEnd = segment.WindowEnd,
                        Points = windowPoints,
                        Statistics = segment.Statistics
                    });
                }

                if (windowViewModels.Count > 0)
                {
                    visualizations.Add(new PersonVisualizationViewModel
                    {
                        Name = group.Key,
                        Windows = windowViewModels
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to render overview chart for {Person}.", group.Key);
            }

            try
            {
                var pdfPath = await _pdfGenerator.GeneratePersonPdfAsync(jobId, group.Key, personRecords, cancellationToken);
                fileNames.Add(Path.GetFileName(pdfPath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate PDF for {Person}.", group.Key);
            }
        }

        if (fileNames.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "No charts could be created from the uploaded data.");
            return View("Index");
        }

        var viewModel = new UploadResultViewModel
        {
            JobId = jobId,
            Files = fileNames,
            Visualizations = visualizations.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        return View("Results", viewModel);
    }

    [HttpGet("download/{jobId}/{fileName}")]
    public IActionResult Download(string jobId, string fileName)
    {
        try
        {
            var path = _pdfGenerator.GetJobFilePath(jobId, fileName);
            if (!System.IO.File.Exists(path))
                return NotFound();

            var stream = System.IO.File.OpenRead(path);
            return File(stream, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to locate file {File} for job {JobId}.", fileName, jobId);
            return NotFound();
        }
    }

    [HttpGet("download-all/{jobId}")]
    public IActionResult DownloadAll(string jobId)
    {
        try
        {
            var payload = _pdfGenerator.BundleJobAsZip(jobId);
            var stamped = $"CSH_Sleep_Charts_{jobId}.zip";
            return File(payload, "application/zip", stamped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bundle PDFs for job {JobId}.", jobId);
            return NotFound();
        }
    }

    [HttpGet("result/{jobId}")]
    public IActionResult Result(string jobId)
    {
        var files = _pdfGenerator.EnumerateJobPdfs(jobId)
            .Select(Path.GetFileName)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Cast<string>()
            .ToList();

        if (files.Count == 0)
        {
            return NotFound();
        }

        var vm = new UploadResultViewModel
        {
            JobId = jobId,
            Files = files,
            Visualizations = Array.Empty<PersonVisualizationViewModel>(),
        };

        return View("Results", vm);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private static IReadOnlyList<SleepChartPoint> BuildPoints(IEnumerable<SleepRecord> records)
    {
        return records
            .OrderBy(r => r.StartDateTime)
            .Select(CreatePoint)
            .ToList();
    }

    private static IReadOnlyList<SleepChartPoint> BuildPoints(IEnumerable<SleepRecord> records, DateTime windowStart, DateTime windowEnd)
    {
        return records
            .Where(r => r.StartDateTime.Date >= windowStart.Date && r.StartDateTime.Date <= windowEnd.Date)
            .OrderBy(r => r.StartDateTime)
            .Select(CreatePoint)
            .ToList();
    }

    private static SleepChartPoint CreatePoint(SleepRecord record)
    {
        return new SleepChartPoint
        {
            StartDateTime = record.StartDateTime,
            EndDateTime = record.GetSleepEnd(),
            DurationHours = record.DurationHours,
            InterruptionCount = record.GetInterruptionCount(),
            AverageInterruptionLengthHours = record.AverageInterruptionLengthHours(),
            HasInterruptions = record.HasInterruptionEvidence()
        };
    }
}

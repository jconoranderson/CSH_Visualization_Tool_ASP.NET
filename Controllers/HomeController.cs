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

    public HomeController(
        ILogger<HomeController> logger,
        SleepCsvLoader csvLoader,
        SleepPdfGenerator pdfGenerator)
    {
        _logger = logger;
        _csvLoader = csvLoader;
        _pdfGenerator = pdfGenerator;
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

        foreach (var group in groups)
        {
            try
            {
                var pdfPath = await _pdfGenerator.GeneratePersonPdfAsync(jobId, group.Key, group.ToList(), cancellationToken);
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
        };

        return View("Results", vm);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

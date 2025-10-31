# Sleep Visualization Tool — ASP.NET Core Rebuild

## High-Level Goals
- Mirror the Flask application's behaviour: ingest CSV uploads, normalise sleep records, build per-person charts, and deliver PDFs (individual and zip bundle).
- Keep the stateless upload flow: each upload produces a job directory under `generated_pdfs/` with PDFs and a downloadable zip.
- Preserve parsing quirks (date normalisation, interruption handling, circular means, 6‑month windows).

## Project Layout
- `SleepVisualizationTool/`
  - `Controllers/HomeController.cs` – index form, upload handler, download endpoints.
  - `Services/`
    - `SleepCsvLoader.cs` – CSV ingestion, raw/parsed detection, parsing helpers.
    - `SleepPdfGenerator.cs` – 6‑month windowing, plotting, PDF assembly.
    - `JobStore.cs` – manages per-upload job folders, cleanup helpers.
  - `Models/`
    - `SleepRecord.cs` – strongly typed representation of parsed rows.
    - `SleepWindow.cs` – computed 6‑month ranges with summary stats.
  - `Views/` – Razor counterparts to the Flask templates.
  - `wwwroot/generated_pdfs/` – matches the Flask storage path.

## Key Dependencies
- `CsvHelper` – robust CSV parsing with schema flex (raw vs parsed layouts).
- `ScottPlot` – renders sleep duration line charts + trend/average overlays to PNG streams.
- `QuestPDF` – composes PDFs per person; embeds ScottPlot charts and summary text.

> NOTE: Packages are referenced in the csproj; restore requires external network access.

## Request Flow
1. `POST /upload`
   - `SleepCsvLoader.LoadAsync(Stream file)` → `IEnumerable<SleepRecord>`.
   - Group by `Name`, normalise dates (fix future, midnight crossover), compute durations.
2. For each person:
   - `SleepPdfGenerator.BuildWindows(records)` → newest-first 6‑month windows.
   - For each window create chart bitmap, statistics, and append to PDF.
   - Save under `generated_pdfs/{jobId}/{safeName}.pdf`.
3. Render `Views/Home/Results.cshtml` with download links and "Download all".
4. `GET /download/{jobId}/{file}` / `GET /download-all/{jobId}` stream individual or zip bundle.

## Outstanding Tasks
- Implement regex-based parsing identical to Flask helpers (`ParseDateField`, `_extract_time_and_period`, etc.).
- Recreate statistical calculations (circular means, trendline, averages).
- Port HTML templates from Jinja → Razor.
- Wire middleware to serve static PDFs safely.

This document serves as the blueprint for the implementation that follows.

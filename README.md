# ASP.NET Sleep Visualization Tool

Rebuild of the original Flask-based “Sleep Plotter” as an ASP.NET Core MVC application.

## Getting Started

```bash
cd CSH_Sleep_Visualization_Tool_AspNet/SleepVisualizationTool
dotnet restore   # requires network access for CsvHelper, ScottPlot, QuestPDF
dotnet run
```

Once running, open `https://localhost:5001` (or the console URL) and upload a CSV exported from the source system. The app accepts:

- Raw `Name,Details` extracts (parses text to timestamps, interruptions, and durations)
- Already-normalised `Name,start_dt,end_dt,duration_hr,interruptions` tables

Each upload produces a unique job ID under `wwwroot/generated_pdfs/` containing per-person PDFs and a bundled ZIP download.

## Key Differences from Flask Version
- **Framework**: ASP.NET Core MVC with Razor views replaces Flask + Jinja templates.
- **Plotting**: Charts are generated with ScottPlot and embedded into PDFs using QuestPDF.
- **Storage**: Job artefacts live under `wwwroot/generated_pdfs/` so they can be served via static file middleware.
- **Dependency Handling**: Requires NuGet packages (`CsvHelper`, `ScottPlot`, `QuestPDF`). Restore them before building or running.

## Cleanup

Job folders are not purged automatically. Remove old directories under `wwwroot/generated_pdfs/` as needed.

# ASP.NET Sleep Visualization Tool

Rebuild of the original Flask-based “Sleep Plotter” as an ASP.NET Core MVC application.

## Getting Started

```bash
cd CSH_Sleep_Visualization_Tool_AspNet/
dotnet restore   # requires network access for CsvHelper, ScottPlot, QuestPDF
dotnet run       # requires the .NET 8 SDK
```

Once running, open `https://localhost:5001` (or the console URL) and upload a CSV exported from the source system. The app accepts:

- Raw `Name,Details` extracts (parses text to timestamps, interruptions, and durations)
- Already-normalised `Name,start_dt,end_dt,duration_hr,interruptions` tables

Each upload now:

- Presents an interactive chart for every individual in the browser (select their name to swap the visualization).
- Generates per-person PDFs plus a ZIP download bundle.
- Stores artifacts under the OS temp folder (`%TEMP%/SleepVisualizationTool/generated_pdfs` or the macOS/Linux equivalent), keeping the web root clean.

## Key Differences from Flask Version
- **Framework**: ASP.NET Core MVC with Razor views replaces Flask + Jinja templates.
- **Plotting**: Charts are generated with ScottPlot, rendered inline in the UI, and embedded into PDFs using QuestPDF.
- **Storage**: Job artefacts live under the system temp path (`{temp}/SleepVisualizationTool/generated_pdfs/`) and are streamed by MVC actions rather than exposed as static files.
- **Dependency Handling**: Targets .NET 8.0 with NuGet packages (`CsvHelper`, `ScottPlot`, `QuestPDF`). Restore them before building or running.

## Cleanup

Job folders are not purged automatically. Remove old directories from the temp folder periodically if disk usage matters.

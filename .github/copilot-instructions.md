<!-- Auto-generated todo section -->
<todos title="Todos" rule="Review steps frequently throughout the conversation and DO NOT stop between steps unless they explicitly require it.">
- No current todos
</todos>

<!-- Add your custom Copilot instructions below -->

## Project overview

Sharptillery is a .NET global tool (PackageId `nyviken-sharptillery`, command `sharptillery`) that load-tests a single HTTP endpoint and produces console stats and an optional HTML report.

Core flow:
- CLI args/YAML are parsed into `ArtilleryConfig` in `src/SharpArtillery/FlagParser.cs` (`ProgramSettings.HandleConfigsAndSettings`).
- `src/SharpArtillery/MainService.cs` maps config into runtime `Settings` and orchestrates test execution.
- `src/SharpArtillery/Manager.cs` owns the run loop: it enqueues `HttpRequestMessage` instances, starts clients, aggregates `DataPoint` results, and exposes `GetProgress` for periodic progress output.
- `src/SharpArtillery/Client.cs` is the worker that dequeues requests and sends them using `HttpClient`, then enqueues `DataPoint` to `Manager.ResponseMessageQueue`.

## Local dev workflows

- Quick run against the included sample API:
	- Start container: use VS Code task `testapi: (re)start container (8080)` (see `.vscode/tasks.json`).
	- Run Sharptillery: `dotnet run --project .\src\SharpArtillery\SharpArtillery.csproj -- -t http://localhost:8080/Test/get -m GET -c 1 -n 10`
- Build the sample API image: run `docker-build.cmd` (builds `testapi:dev`).
- Publish the tool package: `publish.cmd` (uses `$env:NUGET_API_KEY`).

## Configuration conventions

- “Flags mode” (no YAML): `-t` target URL, `-c` virtual users, and exactly one of `-n` (max requests) or `-d` (duration). See help text in `src/SharpArtillery/FlagParser.cs`.
- “YAML mode”: `-y <path>` loads YAML via YamlDotNet (`src/SharpArtillery/Models/Yaml.cs`). Current implementation only reads:
	- `settings.target`
	- `settings.phases[*]`
	- `scenarios[0].flow[0].get.url`
	Use `src/SharpArtillery/config.yaml` as the canonical example.

## Reporting and results

- Results are `DataPoint` structs (`src/SharpArtillery/Models/DataPoint.cs`). HTML reporting groups into 1-second buckets using `DataPoint.RequestTimeLine` and uses `DataPoint.PhaseName` when present.
- HTML report generation is in `src/SharpArtillery/Reporting/HtmlReport.cs` and fills placeholders in the embedded template `src/SharpArtillery/Reporting/Templates/reportTemplate.html`.

## Project-specific coding conventions and gotchas

- Target framework is `net10.0` with `LangVersion=preview` and `ImplicitUsings=disable` (see `src/SharpArtillery/SharpArtillery.csproj`), add explicit `using` directives.
- `CustomHttpClientFactory` typically returns a shared `HttpClient` instance (FactoryEnum `Roger`). Do not dispose per request, dispose the factory.
- YAML “phases” currently run sequentially but `Manager.RunTest()` clears `_responseData` at start, so phase mode does not accumulate results across phases unless you change the storage/aggregation strategy.
- CLI validation uses `ArgumentException`; `MainService.Main` catches these, prints to stderr, and sets `Environment.ExitCode = 2`. Follow this pattern for new flags/validation.

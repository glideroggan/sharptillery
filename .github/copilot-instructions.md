<!-- Auto-generated todo section -->
<todos title="Todos" rule="Review steps frequently throughout the conversation and DO NOT stop between steps unless they explicitly require it.">
- No current todos
</todos>

<!-- Add your custom Copilot instructions below -->

## Project overview

Sharptillery is a .NET global tool (PackageId `nyviken-sharptillery`, command `sharptillery`) that load-tests HTTP endpoints and produces console stats and an optional HTML report. Flags mode targets one endpoint; YAML mode runs phases and, optionally, multi-step per-user flows.

Core flow:
- CLI args/YAML are parsed into `ArtilleryConfig` in `src/SharpArtillery/CommandLineParser.cs` (`BuildConfig`; YAML is loaded by `YamlHelper.ReadYaml`).
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

- “Flags mode” (no YAML): `-t` target URL, `-c` virtual users, and exactly one of `-n` (max requests) or `-d` (duration). See help text in `src/SharpArtillery/CommandLineParser.cs`.
- “YAML mode”: `-y <path>` loads YAML via YamlDotNet (`src/SharpArtillery/Models/Yaml.cs`). `MainService.Run` reads:
	- `settings.target` (base URL)
	- phases: top-level `phases` (or legacy `settings.phases`), each with `vu` and `duration` or `requests`
	- `scenarios[0].flow`: when there are several steps, a think step, or captures, every step runs in order per VU (flow mode, `FlowRunner`). A single GET without captures runs in blast mode.
	Canonical examples: `src/SharpArtillery/config.yaml` (single GET) and `src/SharpArtillery/config-flow-test.yaml` (flow with think).

## Reporting and results

- Results are `DataPoint` structs (`src/SharpArtillery/Models/DataPoint.cs`). HTML reporting groups into 1-second buckets using `DataPoint.RequestTimeLine` and uses `DataPoint.PhaseName` when present.
- HTML report generation is in `src/SharpArtillery/Reporting/HtmlReport.cs` and fills placeholders in the embedded template `src/SharpArtillery/Reporting/Templates/reportTemplate.html`.

## Project-specific coding conventions and gotchas

- Target framework is `net10.0` with `LangVersion=preview` and `ImplicitUsings=disable` (see `src/SharpArtillery/SharpArtillery.csproj`), add explicit `using` directives.
- `CustomHttpClientFactory.CreateClient()` returns one shared `HttpClient` for all virtual users. Do not dispose it per request; the factory owns it and disposes it.
- YAML phases run one after another and their results accumulate: `Manager.RunTest()` does not clear `_responseData` (`src/SharpArtillery/Manager.cs`), and flow mode collects every phase's results in `MainService.RunFlowModeAsync` (`src/SharpArtillery/MainService.cs`).
- Validation throws `ArgumentException` (`Settings.Validate`, auto-tune checks in `Manager.cs`). Neither `Program.cs` nor `CommandLineParser.cs` catches these or sets an exit code, so a new check needs its own handling if it must exit cleanly.

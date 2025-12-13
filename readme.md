Tags: #sharptillery #dotnet #loadtesting #todo

# Sharptillery TODO List

Last updated: 2025-12-13

## Current verified setup

- `testapi` endpoint: `GET http://localhost:8080/Test/get`
- Local runner: `dotnet run --project .\src\SharpArtillery\SharpArtillery.csproj -- -t http://localhost:8080/Test/get -m GET -n 10`
- VS Code task: `sharptillery: run against local testapi` (runs the local project via `dotnet run`)

## Install (global tool)

- Install latest stable:
    - `dotnet tool install -g nyviken-sharptillery --version 0.3.5`
- Upgrade:
    - `dotnet tool update -g nyviken-sharptillery --version 0.3.5`
- Run:
    - `sharptillery -t http://localhost:8080/Test/get -d 00:00:10 -c 10 -o report`

## Publish (NuGet.org)

- Create the package:
    - `dotnet pack .\src\SharpArtillery\SharpArtillery.csproj -c Release --no-build`
- Publish using an environment variable (recommended):
    - `$env:NUGET_API_KEY = "<YOUR_KEY>"`
    - `dotnet nuget push .\src\SharpArtillery\nupkg\nyviken-sharptillery.0.3.5.nupkg --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json`
- Security note:
    - Do not paste API keys into terminals, logs, issues, or chat.
    - If an API key is ever pasted or committed, revoke it immediately and create a new one.

## TODOs (prioritized)

## Completed (2025-12-13)

- Running Sharptillery from VS Code via `dotnet run` against the local `testapi` Docker container on `http://localhost:8080/Test/get`.
- Disposed `HttpResponseMessage` and `HttpRequestMessage`, removed `async void` client runner, and made cancellations non-error.
- Fixed HTTP error classification so non-2xx is counted as error.
- Replaced regex flag parsing with token parsing and added friendly CLI error output on invalid flags (for example missing values).
- Added configurable per-request timeout via `-T HH:MM:SS` and YAML `settings.requestTimeoutSeconds`.
- Added an auto-tune mode to search for max sustainable RPS by increasing clients during the run.
- Defaulted concurrency to 1 when `-c` is omitted, and added validation for invalid concurrency.
- Replaced assertion crashes with friendly CLI errors (for example when neither `-d` nor `-n` is specified).
- Fixed progress reporting to avoid `NaN` when zero requests complete in an interval.
- Clarified RPS reporting (`Average RPS` vs `AutoTune peak (sampled)`), and added a final auto-tune partial-sample so short runs report a realistic peak.
- Improved HTML reporting: 1-second aggregation, summary table, and a cleaner combined chart (latency, RPS, error ratio) (`src/SharpArtillery/Reporting/HtmlReport.cs`, `src/SharpArtillery/Reporting/Templates/reportTemplate.html`).
- Fixed HTML chart layout issues (axis label overlap) and ensured the error ratio series remains visible even when it is near 0% (`src/SharpArtillery/Reporting/Templates/reportTemplate.html`).
- Released `0.3.5` to NuGet (stable) after validating the preview end-to-end.
    - Fixed a broken `0.3.4` NuGet tool build that could produce `0` clients and `NaN` progress.
    - Fixed duration-mode queue handling to avoid assertion termination.
    - Added strict target URL validation (for example `https:/example.com` is rejected).
    - Fixed the `-v` help flag to print version and exit.
    - Prevented crashes when zero requests complete (prints a friendly message instead).

### P0, correctness and safety
- [DEFERRED] Make TLS certificate validation configurable, default to secure validation, add a curl-style `--insecure` flag (`src/SharpArtillery/CustomHttpClientFactory.cs`).
- [x] Dispose `HttpResponseMessage` properly and avoid holding response bodies in memory (`src/SharpArtillery/Client.cs`).
- [x] Fix HTTP error classification so error ratios are meaningful (`src/SharpArtillery/Manager.cs`).
- [x] Replace the regex-based flag parsing with token parsing, validate missing values, and provide friendly errors (`src/SharpArtillery/FlagParser.cs`).

### P1, missing behaviour and feature completeness

- [DEFERRED] Implement `ConstantRps` end-to-end (currently flagged, not enforced) (`src/SharpArtillery/Manager.cs`).
    Notes:
    - Ramping VUs over time is not the same as a fixed request rate, it changes concurrency, and the resulting RPS depends on server latency and client think time.
    - A true constant RPS mode is a throttle, it paces requests to a target rate regardless of VUs (within limits).
    Options:
    - Remove or de-prioritise `ConstantRps`, and instead implement Artillery-style YAML phases like `arrivalRate`, which is closer to “target RPS” in scenario mode.
    Recommendation:
    - Keep `ConstantRps` for request mode, and implement `arrivalRate` later for YAML scenario mode.
- [DEFERRED] Improve phase support, per-phase reporting, and phase metadata in results (`src/SharpArtillery/MainService.cs`).
    Notes:
    - Today, YAML phases run sequentially, but only a very small subset of YAML is used (first scenario, first flow, first GET URL).
    - “Phase metadata” means storing which phase produced each datapoint, for example phase index or name, phase VU, phase duration, and the phase start time.
    - “Per-phase reporting” means console progress includes the current phase details, and the HTML report can group results by phase (separate series or separate charts).
    Minimum acceptance:
    - Every `DataPoint` created during a YAML run is tagged with a phase identifier.
    - The final report can show results per phase instead of mixing or overwriting.
- [DEFERRED] Persist or aggregate results between phases instead of clearing state (store to temporary in-memory storage or a file) (`src/SharpArtillery/MainService.cs`).
    Notes:
    - `Manager.RunTest()` clears `_responseData`, so if you run multiple phases the later phase can overwrite earlier phase results.
    - Persisting results means collecting all datapoints across phases into one list (or one list per phase) and using that for the final report.
    Options:
    - File-backed: append per-request or per-second aggregates to a CSV or JSONL during the run, then read once at the end to generate the report.
- [DEFERRED] Re-enable Excel reporting (`.xlsx`) and remove interactive prompts from the export path by adding a quiet mode flag (`src/SharpArtillery/MainService.cs`, `src/SharpArtillery/Reporting/MyExcel.cs`).
    Notes:
    - Agree, this is best after HTML reporting is stable, and after we remove interactive prompts so CI runs are possible.
- [x] Make request timeouts configurable via flags and YAML (`src/SharpArtillery/Client.cs`, `src/SharpArtillery/FlagParser.cs`, `src/SharpArtillery/Models/Yaml.cs`).
    Notes:
    - CLI: `-T HH:MM:SS` (default remains `00:00:05`).
    - YAML: `config.settings.requestTimeoutSeconds: <int>`.
- [x] Add auto-tune to find highest sustainable RPS by adjusting clients during runtime (`src/SharpArtillery/Manager.cs`, `src/SharpArtillery/FlagParser.cs`).
    Notes:
    - Duration mode only, `-A` requires `-d`, and is not supported with `-n`.
        - Report output:
            - `Average RPS` is the whole-run average.
            - `AutoTune peak (sampled)` is the best sampled interval (and includes a final partial sample on shutdown).
    - Flags:
      - `-A` enable auto-tune
      - `-M <number>` max clients (default: `256`)
      - `-S <number>` step size (default: `5`)
      - `-I HH:MM:SS` tune interval (default: `00:00:05`)
      - `-E <ratio>` max error ratio, for example `0.02` (default: `0.02`)
- [ ] Improve auto-tune to converge on a stable client count (supports backing off, not only scaling up) (`src/SharpArtillery/Manager.cs`).

### P2, reporting quality

- [x] Sample and aggregate results at 1 second intervals for cleaner graphs (`src/SharpArtillery/Reporting/HtmlReport.cs`).
- [x] Add the full target endpoint to report metadata, plus a short “load test summary” table (VU, duration, requests, RPS, error ratio, percentiles) (`src/SharpArtillery/Reporting/HtmlReport.cs`, `src/SharpArtillery/Reporting/Templates/reportTemplate.html`).

### Diagnostics and observability

- [ ] Connect `dotnet-counters` to the `testapi` container to collect metrics during runs (`src/testapi/Program.cs`).
- [ ] Create a reusable metrics base image or sidecar (for example using `dotnet-counters` and or `dotnet-monitor`) to capture server-side metrics during runs (request queue, thread pool, GC, connection counts) for any target API container.
- [ ] Add trace logging where marked, and consider high-performance logging patterns for the progress loop (`src/SharpArtillery/Manager.cs`, `src/SharpArtillery/MainService.cs`).

### Cleanup

- [ ] Review client lifecycle reuse in `PrepareForTest`, and ensure clients are not leaked across runs (`src/SharpArtillery/Manager.cs`).
- [ ] Revisit `MaxConnectionsPerServer` sizing, and consider scaling based on VUs (`src/SharpArtillery/CustomHttpClientFactory.cs`).
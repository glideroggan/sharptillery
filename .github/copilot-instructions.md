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
- `src/SharpArtillery/Manager.cs` owns the run loop (blast mode). `PrepareForTest` builds a `RequestTemplate` for the phase, creates the clients and starts their tasks parked on a `TaskCompletionSource` start gate (`Manager.Started`). `RunTest` is async: it opens the gate, arms the duration timer, and awaits a `Channel<DataPoint>` together with a 1 s `PeriodicTimer`, so the loop sleeps between results and progress lines (no busy loop). It publishes a new `Progress` snapshot per tick (`GetProgress`): requests, error ratio and "% done" are per phase, RPS and mean latency are for the last tick.
- `src/SharpArtillery/Client.cs` is the worker: it waits at the gate, then loops: `Manager.TryClaimRequest()` (the shared Interlocked `-n` counter; always true with `-d`), the rate slot (if `-r`), `RequestTemplate.Build()` right before the send, `HttpClient.SendAsync`, and `Manager.ResultWriter.WriteAsync(dataPoint, CancellationToken.None)`. Latency is one reading of `Manager.TimeProvider` (the Stopwatch outside tests), counted from the send; the result's only time is its point on `Manager.GlobalTimeTimer` (`RequestTimeLine`), with no `DateTime` fields.
- `-r`: `RunTest` opens one `RateSchedule` per phase (`src/SharpArtillery/RateSchedule.cs`) before the gate. Slot n is due at the phase start + n / rps; a client takes the next slot (Interlocked) and waits on the run's clock until it is due, so the wait counts from the planned slot, not from the client's last response. When every client is busy the due slots are sent as clients come free (the run catches up). `RateSchedule.FellBehind` compares the slots taken with the slots due at the last take; when it is true, `Manager.RateShortfall` holds one line (target, reached, and what `-c` clients at the mean latency can do) that `MainService` prints after the phase or run. Flow mode (`FlowRunner`) does not use `-r`.
- Phase end: the duration timer (or the `-n` counter running out, or auto-tune) cancels the clients; `RunTest` then awaits every client task, completes the channel and drains it, so no result can land in the next phase. A request still in flight at that moment is dropped and counted in `Progress.CancelledAtPhaseEnd` (printed as "cancelled at phase end"), not recorded as a 500.
- `Manager` takes an optional `TimeProvider` (progress ticker, duration timer, auto-tune interval, the `-r` schedule's waits, request latency). Tests pass a `FakeTimeProvider` and advance it instead of sleeping. The per-request timeout still uses the system clock.

## Local dev workflows

- Quick run against the included sample API:
	- Start container: use VS Code task `testapi: (re)start container (8080)` (see `.vscode/tasks.json`).
	- Run Sharptillery: `dotnet run --project .\src\SharpArtillery\SharpArtillery.csproj -- -t http://localhost:8080/Test/get -m GET -c 1 -n 10`
- Run the tests: `dotnet test Sharptillery.sln`. They need no network or test API.
- Build the sample API image: run `docker-build.cmd` (builds `testapi:dev`).
- Benchmark gate: `pwsh -NoProfile -File tools/bench.ps1 -StartApi -StopApi` builds Release, runs the card's short commands against the container at low priority and prints requests, req/s, tool CPU / wall and peak memory. `-Baseline <other checkout>` runs the same commands against a second build for a before/after table; `-Runs slow,c64` picks runs. `-StartApi` starts the container without a CPU limit (the VS Code task's `--cpus=0.5` caps the API near 5k req/s); `-ApiCpus 0.5` restores that limit.
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
- Every result stays in memory until the report is written, so `DataPoint` holds only fields that the console summary or the report reads; `DataPointTests` keeps it at 72 bytes or less.
- `-e csv` (`src/SharpArtillery/Reporting/CsvReport.cs`) writes one row per result as the run reads it, through `Manager.Csv` (blast mode) and `DrainResults` in `MainService` (flow mode, once a second). The file is opened before the first request; a failed write is reported when the report closes, and the run goes on. Excel output was removed (no `MyExcel`, no OpenXml).
- HTML report generation is in `src/SharpArtillery/Reporting/HtmlReport.cs` and fills placeholders in the embedded template `src/SharpArtillery/Reporting/Templates/reportTemplate.html`. `PrepareReportData` makes one pass over the results plus one sort of the OK latencies; `HtmlReportOnePassTests` compares its numbers with the earlier LINQ version (`tests/SharpArtillery.Tests/ReferenceReport.cs`).
- The charts are Chart.js 4.5.1 (MIT), vendored unchanged as the embedded resource `Reporting/Templates/chart.umd.min.js` and written into the report at `<# CHART JS #>`, so a report needs no network. The tables are plain HTML built with `textContent`.

## Tests

- `tests/SharpArtillery.Tests` is an xUnit project in `Sharptillery.sln`. `IsPackable` is false, so it never goes into the tool package. `SharpArtillery.csproj` makes internals visible to it.
- No real HTTP: `Stubs/StubHttpHandler.cs` answers from a delegate and records each request. Blast mode gets it through `StubHttpClientFactory` (an `ICustomHttpClientFactory`). Flow mode gets it through the optional `handler` parameter of `VirtualUser`, `FlowRunner` and `MainService.RunFlowModeAsync`. That parameter defaults to null, and then each VU builds its own handler and cookie jar.
- Test parallelization is off (`AssemblyInfo.cs`): the run-loop tests keep cores busy, and two of them measure real time (the start-gate grace period and the 100 ms Stopwatch latency).
- Run-loop tests (`ManagerTests.cs`) drive phases with `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`): a stub that blocks until a `TaskCompletionSource` is set, then `clock.Advance(...)` to end the phase or fire a progress tick. Every await is bounded by `Patience` (30 s); a poll (`WaitUntilAsync`) is used only where no awaitable signal exists.
- Rate tests (`RateLimitTests.cs`) step a `Stubs/ParkingClock` (a `FakeTimeProvider` that tracks its pending one-shot timers) 1 ms at a time, and only once every client waits on it and no due timer is left unfired, so the counts are the same on every run.
- Red tests for later work are `[Fact(Skip = "slice N")]` with `[Trait("slice", "N")]`. The slice that fixes the bug removes the `Skip`. To see them fail today, remove the `Skip` and run `dotnet test --filter "slice=3"`.

## Project-specific coding conventions and gotchas

- Target framework is `net10.0` with `LangVersion=preview` and `ImplicitUsings=disable` (see `src/SharpArtillery/SharpArtillery.csproj`), add explicit `using` directives.
- Blast mode: `Client`s share the one `HttpClient` that `CustomHttpClientFactory.CreateClient()` returns. Do not dispose it per request. `CustomHttpClientFactory` implements `IDisposable`, but `MainService.Run` creates it without `using` and nothing disposes it. Its `MaxConnectionsPerServer` is the number of users the run can reach (`-c`, the auto-tune maximum, the largest YAML phase), one connection per user.
- A bad header or URL fails in `PrepareForTest` (it builds one request from the template up front), not once per request during the run.
- Flow mode: `CustomHttpClientFactory` is created but not used in flow mode. Each `VirtualUser` builds its own `HttpClient` with its own cookie jar (`src/SharpArtillery/VirtualUser.cs`), and `VirtualUser.Dispose` disposes that client. A handler passed in (tests) is not disposed by the VU.
- YAML phases run one after another and their results accumulate: `Manager.RunTest()` does not clear `_responseData` (`src/SharpArtillery/Manager.cs`), and flow mode collects every phase's results in `MainService.RunFlowModeAsync` (`src/SharpArtillery/MainService.cs`).
- `Settings.Validate` throws `ArgumentException`, and neither `Program.cs` nor `CommandLineParser.cs` catches it. The auto-tune `ArgumentException`s in `Manager.RunAutoTuneAsync` are thrown inside the `Task.Run` that `Manager.RunTest` starts, and the `catch` around `autoTuneTask.WaitAsync` at the phase end swallows them, so they never reach the user. The run goes on without auto-tune and prints no error about it (the "AutoTune enabled" line still prints first).
- New CLI flag checks should follow the System.CommandLine pattern in `CommandLineParser.AddValidators` (`rootCommand.Validators.Add(result => ... result.AddError(...))`, `src/SharpArtillery/CommandLineParser.cs`), so the error reaches the user through the parser.

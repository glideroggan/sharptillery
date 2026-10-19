# Sharptillery roadmap

Open work, ideas and code-review findings. Done items are listed at the end.

## Metrics and reporting ideas

Client-observed metrics that are usually most useful:

- Latency percentiles: p50, p90, p95, p99 (overall and per phase), plus min and max.
- Status code breakdown: 2xx, 3xx, 4xx, 5xx, plus top specific codes (for example 429, 503).
- Error taxonomy: timeout, DNS, TLS, connection reset, HTTP protocol errors, request cancellation.
- Bytes in and out: response size distribution and throughput (bytes per second).
- SLA or Apdex style thresholds: percent under X ms, percent over Y ms.
- Load generator health (still client-side): achieved concurrency vs configured, queueing or scheduling delay, process CPU and memory, and GC counters.

HTML report sections that tend to be actionable:

- Summary tiles: RPS, p50, p95 and p99, error rate, 4xx rate, 5xx rate, bytes per second.
- Time series: RPS over time, latency percentiles over time, stacked status codes over time.
- Distributions: latency histogram with percentile markers.
- Tables: top status codes, top error categories, worst time buckets (for example "worst 1 second by p99").

Server and container stats (CPU, memory, GC, thread pool, connection counts) are valuable but need more tooling to collect reliably. For now the focus is on client-observed metrics.

## YAML scenario phases and flows

No open items: the YAML phase and flow work listed here is done, see Done.

## Correctness and safety

- [ ] Make TLS certificate validation configurable, default to secure validation, add a curl-style `--insecure` flag if needed (`src/SharpArtillery/CustomHttpClientFactory.cs`).

## Feature completeness

- [ ] Implement `ConstantRps` end-to-end or remove the flag, and document the chosen behaviour (`src/SharpArtillery/Manager.cs`).
- [ ] Improve auto-tune to converge on a stable client count (supports backing off, not only scaling up) (`src/SharpArtillery/Manager.cs`).

## Diagnostics and observability

- [ ] Connect `dotnet-counters` to the `testapi` container to collect metrics during runs (`src/testapi/Program.cs`).
- [ ] Create a reusable metrics base image or sidecar (for example using `dotnet-counters` or `dotnet-monitor`) to capture server-side metrics during runs (request queue, thread pool, GC, connection counts) for any target API container.
- [ ] Add trace logging where marked, and consider high-performance logging patterns for the progress loop (`src/SharpArtillery/Manager.cs`, `src/SharpArtillery/MainService.cs`).

## Cleanup

- [ ] Review client lifecycle reuse in `PrepareForTest`, and ensure clients are not leaked across runs (`src/SharpArtillery/Manager.cs`).

## Code review actions

### P2 - Nice to have

- **AP-004: Mixing concerns in MainService.Run().** `MainService.Run` gets an already-parsed `flags.YamlConfig` (YAML is read in `CommandLineParser.BuildConfig`), but it still handles config checks, the blast and flow mode branches, phase loops, non-phase runs and report writing in one method. Suggested fix: extract smaller focused methods or classes.
- **AP-006: Console output scattered everywhere.** `Manager.cs`, `MainService.cs`, `Client.cs` use `Console.Out.WriteLine` instead of ILogger. Impact: hard to control verbosity, no log levels.

### P3 - Cleanup (low priority)

- **BUG-001: IDisposable cosmetic issue.** `Manager.cs` implements `IDisposable`, but this is a CLI tool and the OS reclaims resources on exit anyway. Not a real bug.

## Done

### Reporting

- [x] Excel (`.xlsx`) reporting removed (DEAD-002): `-e csv` writes one row per request as the run reads its results (`src/SharpArtillery/Reporting/CsvReport.cs`).

### YAML scenarios

- [x] Multi-step per-client YAML flows (`get`, `post`, `put`, `delete` and `think` steps) run in order by `FlowRunner`.
- [x] Multiple flow steps per scenario: every step runs per virtual user, not only `flow[0]`.
- [x] `settings.phases` maps to runtime: each phase sets its own VUs, duration or requests (`src/SharpArtillery/MainService.cs`).
- [x] Each `DataPoint` carries its phase name (`PhaseName`, `src/SharpArtillery/Client.cs`, `src/SharpArtillery/FlowExecutor.cs`), so reports group by phase.
- [x] Results accumulate across phases instead of being overwritten (`Manager.RunTest()` keeps `_responseData`; flow mode collects every phase in `MainService.RunFlowModeAsync`).
- [x] Per-phase console progress and totals (`src/SharpArtillery/MainService.cs`), and a per-phase breakdown in the HTML report (`src/SharpArtillery/Reporting/HtmlReport.cs`).

### Client-side metrics

- [x] Timeout detection for per-request timeouts (based on `-T`), and timeout counts in the HTML report.
- [x] Status-family counts (2xx, 3xx, 4xx, 5xx) in the HTML report.
- [x] Status-code distribution (histogram) in the HTML report.
- [x] Error-type distribution (histogram) in the HTML report, grouped by timeout and HTTP status codes, and enriched with exception types when present.
- [x] Response `Content-Length` capture (when provided by the server), plus basic response length stats in the HTML report.

### HTML report UX

- [x] Short runs (single time bucket) clamp the time axis to start at 0 seconds for readability.
- [x] Status and error histograms are rendered as charts (not tables) for cleaner scanning.

### Test API simulation

- [x] `simulateErrors=1` mode in `testapi` produces a realistic mixture of outcomes for report testing.

### Run loop

- [x] Blast mode without a busy loop: the clients write to a `Channel<DataPoint>` that `Manager.RunTest` awaits together with a 1 s `PeriodicTimer` (`src/SharpArtillery/Manager.cs`). The `-c 8 -d 5s` run against a 500 ms endpoint went from 5.8 s to under 1 s of tool CPU.
- [x] Requests are built from a per-phase `RequestTemplate` right before the send; the pre-built queue and its top-ups are gone, and a shared Interlocked counter hands out the `-n` slots.
- [x] A request still in flight at the phase end is dropped and counted as "cancelled at phase end" instead of a 500, and nothing from one phase lands in the next: the phase end cancels, awaits every client, completes the channel and drains it.
- [x] The error ratio and "% done" are per phase, not per last second.
- [x] `MaxConnectionsPerServer` is the number of users the run can reach (`src/SharpArtillery/CustomHttpClientFactory.cs`).
- [x] `tools/bench.ps1` measures the card's runs (requests, req/s, tool CPU / wall, peak memory) against the local testapi, with an optional baseline checkout.

### Code review actions completed

| ID | Summary |
|----|---------|
| SIMP-001 | Migrated CLI from custom FlagParser to System.CommandLine |
| DEAD-001 | Implemented per-client rate limiting (`-r`/`--rps`) |
| SIMP-005 | Replaced O(n²) RPS calculation with O(n) sliding window |
| AP-001 | Extracted ConsoleReporter from Manager |
| AP-003 | Unified settings mapping with `ToSettings()` method |
| SIMP-004 | Extracted `IsError()` to DataPoint instance method |
| SIMP-003 | Extracted `Percentile()` to StatsUtils |
| AP-005 | Centralized settings validation in `Settings.Validate()` |
| SIMP-002 | Removed FactoryEnum dead code, simplified CustomHttpClientFactory |
| BUG-004 | Removed stale pragma and unused `_logger` from MainService |
| BUG-005 | Renamed `Done` to `_done` in Manager |
| DEAD-003 | Removed unused `_httpClientFactory` field (part of SIMP-002) |
| DEAD-004 | Phase results accumulate across phases; the phase-overwrite TODO is gone (see the YAML scenarios entries above) |
| INV-001 | Multi-phase YAML checked against a local testapi with `config.yaml`: two phases ran end to end, the report total (25264) equals the sum of the phases (7926 + 17338), the HTML has a Phase Breakdown section, and the file parses |
| AP-002 | `Manager.RunTest` is `async Task`; `MainService` awaits it instead of starting a `Thread` |
| AP-007 | The start gate is a `TaskCompletionSource` that the clients await; no blocking `WaitOne` |
| BUG-002 | `_responseData` is a plain `readonly` list again. It was never a data race: the real race was a result enqueued after the run loop had stopped, which the phase-end drain removes |
| BUG-003 | One `Stopwatch` reading per request gives the latency; the `DateTime` fields are derived from it |
| SIMP-006 | `TopupSettings` is gone with the request queue |

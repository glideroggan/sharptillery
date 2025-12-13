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

## Metrics and reporting (ideas)

### Client-observed metrics that are usually most useful

- Latency percentiles: p50, p90, p95, p99 (overall and per phase), plus min and max.
- Status code breakdown: 2xx, 3xx, 4xx, 5xx, plus top specific codes (for example 429, 503).
- Error taxonomy: timeout, DNS, TLS, connection reset, HTTP protocol errors, request cancellation.
- Bytes in and out: response size distribution and throughput (bytes per second).
- SLA or Apdex style thresholds: percent under X ms, percent over Y ms.
- Load generator health (still client-side): achieved concurrency vs configured, queueing or scheduling delay, process CPU and memory, and GC counters.

### HTML report sections that tend to be actionable

- Summary tiles: RPS, p50 and p95 and p99, error rate, 4xx rate, 5xx rate, bytes per second.
- Time series: RPS over time, latency percentiles over time, stacked status codes over time.
- Distributions: latency histogram with percentile markers.
- Tables: top status codes, top error categories, worst time buckets (for example “worst 1 second by p99”).

### Server and container stats

- For now, focus on client-observed metrics.
- Server or container stats (CPU, memory, GC, thread pool, connection counts) are valuable, but need additional work and tooling to collect reliably.

### Next up (YAML scenario phases and flows)

- [ ] Expand YAML support from “single GET” to full multi-step per-client flows, for example `get`, `post`, and sequential steps that each virtual user executes in order (`src/SharpArtillery/Models/Yaml.cs`, `src/SharpArtillery/MainService.cs`, `src/SharpArtillery/Client.cs`).
- [ ] Support multiple `flow` entries and produce one request per flow step per virtual user, not only `flow[0]`.
- [ ] Define how `settings.phases` maps to runtime, for example per-phase VUs, per-phase duration or requests, and phase transitions without losing results.
- [ ] Tag each `DataPoint` with phase metadata (phase index and or name) so reports can group by phase.
- [ ] Persist or aggregate results across phases instead of clearing or overwriting (`src/SharpArtillery/Manager.cs`, `src/SharpArtillery/MainService.cs`).
- [ ] Add per-phase console progress output and optional per-phase sections in the HTML report (`src/SharpArtillery/Reporting/HtmlReport.cs`).

### Correctness and safety

- [ ] Make TLS certificate validation configurable, default to secure validation, add a curl-style `--insecure` flag if needed (`src/SharpArtillery/CustomHttpClientFactory.cs`).

### Feature completeness

- [ ] Implement `ConstantRps` end-to-end or remove the flag, and document the chosen behaviour (`src/SharpArtillery/Manager.cs`).
- [ ] Improve auto-tune to converge on a stable client count (supports backing off, not only scaling up) (`src/SharpArtillery/Manager.cs`).
- [ ] Re-enable Excel reporting (`.xlsx`) and remove interactive prompts from the export path by adding a quiet mode flag (`src/SharpArtillery/MainService.cs`, `src/SharpArtillery/Reporting/MyExcel.cs`).

### Diagnostics and observability

- [ ] Connect `dotnet-counters` to the `testapi` container to collect metrics during runs (`src/testapi/Program.cs`).
- [ ] Create a reusable metrics base image or sidecar (for example using `dotnet-counters` or `dotnet-monitor`) to capture server-side metrics during runs (request queue, thread pool, GC, connection counts) for any target API container.
- [ ] Add trace logging where marked, and consider high-performance logging patterns for the progress loop (`src/SharpArtillery/Manager.cs`, `src/SharpArtillery/MainService.cs`).

### Cleanup

- [ ] Review client lifecycle reuse in `PrepareForTest`, and ensure clients are not leaked across runs (`src/SharpArtillery/Manager.cs`).
- [ ] Revisit `MaxConnectionsPerServer` sizing, and consider scaling based on VUs (`src/SharpArtillery/CustomHttpClientFactory.cs`).
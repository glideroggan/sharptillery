# Sharptillery

Sharptillery is a command-line HTTP load testing tool for .NET. It sends requests from a set of virtual users, prints latency and error statistics to the console, and can write an HTML report.

[![NuGet](https://img.shields.io/nuget/v/nyviken-sharptillery.svg)](https://www.nuget.org/packages/nyviken-sharptillery)

![Sharptillery HTML report](https://raw.githubusercontent.com/glideroggan/sharptillery/main/docs/images/report.png)

## Install

The .NET 10 SDK is needed to install the tool, because `dotnet tool install` is an SDK command.

```powershell
dotnet tool install -g nyviken-sharptillery
```

Upgrade with `dotnet tool update -g nyviken-sharptillery`.

## Quick start

Run 4 virtual users against an endpoint for 10 seconds and write an HTML report named `report.html`:

```powershell
sharptillery -t https://example.com/api/endpoint -m GET -c 4 -d 00:00:10 -o report
```

Use `-n` instead of `-d` to send a fixed number of requests. Use `-d` or `-n`, not both. Run `sharptillery --help` for the full list.

| Option | Meaning |
|--------|---------|
| `-t, --target` | Target URL (http or https) |
| `-c, --clients` | Virtual users (parallel clients), default 1 |
| `-n, --requests` | Maximum number of requests |
| `-d, --duration` | Run length, `HH:MM:SS` |
| `-T, --timeout` | Per-request timeout, `HH:MM:SS`, default 5 seconds |
| `-m, --method` | HTTP method: GET, POST, PUT, DELETE, PATCH, HEAD or OPTIONS (default GET); any other value is an error |
| `-b, --body` | JSON body content; single quotes may stand for double quotes around strings. Invalid JSON is an error |
| `-H, --header` | Header as `Name:Value`, can be repeated. A value without a colon or without a name is an error. `Content-*` headers (such as `Content-Type`) go on the request body |
| `-o, --output` | Report name without extension (writes an HTML report) |
| `-e, --extension` | Report type, `html` (the only one for now); any other value is an error |
| `-y, --yaml` | Run a YAML scenario file (see below) |
| `-r, --rps` | Target requests per second, spread across the clients |
| `-A, --autotune` | Find the highest sustainable RPS (duration mode only) |

## YAML scenarios

A YAML file describes phases (virtual users and duration per phase) and a flow that each virtual user runs in order. A `think` step pauses for the given number of seconds.

```yaml
settings:
  target: "https://example.com"
  timeout: 5

phases:
  - name: "Flow Test"
    duration: 10
    vu: 3

scenarios:
  - name: "Multi-step flow test"
    flow:
      - get:
          url: "/api/endpoint"
      - think: 1
      - get:
          url: "/api/endpoint"
```

Run it with:

```powershell
sharptillery -y flow.yaml -o report
```

Each phase prints a progress line every second and a completed line. After the last phase the run prints the same latency summary and percentiles as a flags run, over every phase together.

### What a YAML file can hold

The file is checked before any request is sent. A key that is not in the list below is an error that names the key and its line, so nothing in the file is ignored without a word.

| Key | Meaning |
|-----|---------|
| `settings.target` | Base URL; each step's `url` is added to it |
| `settings.timeout` | Per-request timeout in seconds (replaces `-T`) |
| `settings.report` | Report type, as `-e`: `html` is the only one. A typed `-e` wins, and `-o` still turns the report on |
| `phases` | Run one after another. Each has `name`, `vu` (users) and either `duration` (seconds) or `requests` |
| `requests` | The total for the phase, shared by its users (`requests: 2` with `vu: 5` sends 2). Think steps do not count |
| `duration` | Ends the phase when it runs out, also in the middle of a step: a request still waiting is counted as "cancelled at phase end", not as an error |
| `scenarios` | Exactly one scenario, with a `name` and a `flow` |
| flow steps | `get`, `post`, `put` or `delete` with `url`, `headers`, `json` or `body`, and `capture` (`json: "$.path"` or `header: Name`, with `as: variable`); or `think: <seconds>` |

Older files may still use `settings.phases` (instead of top-level `phases`) and `settings.requestTimeoutSeconds` (instead of `settings.timeout`); both are read.

`{{ variable }}` in a `url`, header value, top-level `json` value or `body` is replaced by a captured value. `Content-*` headers (such as `Content-Type`) go on the request body and replace the default type of a `json` body.

A flow of one request without captures runs in blast mode, the same loop as a flags run. Any other flow runs per user: each user runs the steps in order, with its own cookies and captured values.

Not supported, and an error when the file has it:

- several scenarios (weighted scenarios); `weight` on the one scenario is accepted and has no effect;
- Artillery's arrival-rate phases: `arrivalRate`, `arrivalCount`, `rampTo`, `maxVusers`;
- `excludeGraph`: every phase is in the report;
- `settings.report` other than `html`.

## Development

`src/SharpArtillery` is the tool. `src/testapi` is a small target API with an endpoint that can simulate errors and slow responses, useful for trying the tool locally:

```powershell
dotnet run --project src/testapi --urls http://localhost:8080
dotnet run --project src/SharpArtillery -- -t "http://localhost:8080/Test/get?simulateErrors=1" -m GET -c 4 -d 00:00:10 -o report
```

Run the tests with `dotnet test Sharptillery.sln`. They need no network and no test API.

Planned work and ideas are in [docs/roadmap.md](https://github.com/glideroggan/sharptillery/blob/main/docs/roadmap.md).

## License

MIT. See [LICENSE](https://github.com/glideroggan/sharptillery/blob/main/LICENSE).

The HTML report draws its charts with [Chart.js](https://www.chartjs.org) 4.5.1 (MIT licence, Copyright (c) 2014-2024 Chart.js Contributors). Chart.js is written into every report together with its licence notice, so a report opened without a network still shows its charts.

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
| `-m, --method` | HTTP method: GET, POST or PUT |
| `-b, --body` | JSON body content |
| `-H, --header` | Header as `Name:Value`, can be repeated |
| `-o, --output` | Report name without extension (writes an HTML report) |
| `-e, --extension` | Report type, `html` |
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

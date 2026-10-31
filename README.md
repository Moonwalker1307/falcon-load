# falcon-load

An API load tester with a web UI. Configure the HTTP requests you want to hammer, choose how hard and for how long, then watch throughput, latency percentiles, status codes and errors update live.

Built with .NET 10: an ASP.NET Core host with an interactive Blazor Server UI, and a standalone load engine library.

## Run it

```bash
dotnet run --project src/FalconLoad.Web
```

Then open http://localhost:5299 (or https://localhost:7002 with `--launch-profile https`).

> Only load test APIs you own or have permission to test. falcon-load sends real traffic and is meant to run on your own machine or a private network. Don't expose it publicly: it will send requests to any URL it's given.

## Features

- **Request editor**: method, URL, headers, body and content type, with a "Send once" button to check the response before you load test.
- **Multi-step scenarios**: add several requests; each virtual user runs them in order and loops.
- **Dynamic values** in URLs, headers and bodies: `{{$guid}}`, `{{$randomInt}}`, `{{$randomInt:1:100}}`, `{{$timestamp}}`, `{{$isoTimestamp}}`, `{{$vu}}`, `{{$iteration}}`.
- **Load profile**: virtual users (concurrency), stop after a duration or a request count, ramp-up, optional requests-per-second cap, per-request timeout, redirect and TLS options.
- **Success criteria**: expected status codes per request (e.g. `200,201` or `200-299,404`); defaults to any 2xx/3xx.
- **Live results**: requests, throughput, error rate, latency (mean, p50, p90, p95, p99, max), per-second throughput and latency charts, per-request breakdown, status code and error tables.
- **Saved tests** persisted to `App_Data/plans.json` (change with the `FalconLoad:DataDirectory` setting). The last 50 runs are kept in memory and can be exported as JSON.
- **JSON API** for scripting and CI.

## JSON API

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/plans` | List saved tests |
| `POST` | `/api/plans` | Create or update a saved test |
| `DELETE` | `/api/plans/{id}` | Delete a saved test |
| `POST` | `/api/plans/{id}/run` | Start a saved test |
| `POST` | `/api/runs` | Start an ad-hoc test (plan in the body, not saved) |
| `GET` | `/api/runs` | List recent runs |
| `GET` | `/api/runs/{id}` | Live or final results |
| `GET` | `/api/runs/{id}/export` | Results as a JSON download |
| `POST` | `/api/runs/{id}/stop` | Stop a running test |
| `POST` | `/api/probe` | Send a single request and return the response |

Example:

```bash
curl -X POST http://localhost:5299/api/runs -H 'content-type: application/json' -d '{
  "name": "Products smoke",
  "requests": [
    { "method": "GET", "url": "https://localhost:5001/api/products?page={{$randomInt:1:20}}" },
    { "method": "POST", "url": "https://localhost:5001/api/orders",
      "headers": [{ "name": "Authorization", "value": "Bearer <token>" }],
      "body": "{ \"id\": \"{{$guid}}\" }", "expectedStatusCodes": "201" }
  ],
  "profile": { "concurrency": 50, "stopCondition": "Duration", "durationSeconds": 60, "rampUpSeconds": 10, "ignoreTlsErrors": true }
}'
```

## Project layout

```
src/FalconLoad.Core          Load engine: plan model, runner, metrics (no web dependencies)
src/FalconLoad.Web           ASP.NET Core host, Blazor UI and JSON API
tests/FalconLoad.Core.Tests  xUnit tests for the engine and metrics
```

How the engine works: each virtual user is a task that loops over the plan's requests through a shared `SocketsHttpHandler`. A request is timed until its full response body has been read. Latencies go into a fixed-size log-linear histogram (about 1% precision), so percentiles stay cheap and memory stays flat however long a test runs. Requests still in flight when a test stops are discarded rather than counted as errors.

## Development

```bash
dotnet build
dotnet test
```

# falcon-load

![falcon-load](docs/logo/falcon-load-logo.png)

An API load tester with a web UI. Configure the HTTP requests you want to hammer, choose how hard and for how long, then watch throughput, latency percentiles, status codes and errors update live.

Built with .NET 10: an ASP.NET Core host with an interactive Blazor Server UI, and a standalone load engine library.

## Run it

```bash
dotnet run --project src/FalconLoad.Web
```

Then open http://localhost:5299 (or https://localhost:7002 with `--launch-profile https`).

> Only load test APIs you own or have permission to test. falcon-load sends real traffic and is meant to run on your own machine or a private network. Don't expose it publicly: it will send requests to any URL it's given.

## Features

- **Request editor**: method, URL, headers, body and content type, with a "Send once" button to check the response before you load test. Problems are shown next to the request or setting they belong to, and the editor warns before you leave with unsaved changes.
- **Multi-step scenarios**: add several requests; each virtual user runs them in order and loops.
- **Dynamic values** in URLs, headers and bodies: `{{$guid}}`, `{{$randomInt}}`, `{{$randomInt:1:100}}`, `{{$timestamp}}`, `{{$isoTimestamp}}`, `{{$vu}}`, `{{$iteration}}`.
- **Parameter lists**: name a list of values, such as `userIds` = 17, 42, 99, and write `{{userIds}}` in a URL, header value or body. Each iteration of a virtual user takes one value from every list, so all requests in that iteration use the same value. A list hands out its values in order, shared by all virtual users and starting over at the end, or picks one at random. "Send once" uses the first value. Placeholders that match no list are sent as written.
- **Load profile**: virtual users (concurrency), stop after a duration or a request count, ramp-up, optional requests-per-second cap, per-request timeout, redirect and TLS options.
- **Load stages**: instead of a constant load, give the test stages whose targets are virtual users or requests per second. Start from a **step** (climb in equal steps), **spike** (baseline, sudden burst, recovery) or **soak** (ramp up, then hold for a long time) template, or write your own: each stage holds its target or ramps to it in a straight line. A preview chart shows the planned load as you edit, and the run page shows the stage the test is in.
- **Success criteria**: expected status codes per request (e.g. `200,201` or `200-299,404`); defaults to any 2xx/3xx.
- **Live results**: a pixel-art falcon flies along the progress bar while a test runs. Requests, throughput, error rate, latency (mean, p50, p90, p95, p99, max), per-second throughput and latency charts, per-request breakdown, status code and error tables.
- **Saved tests** persisted to `App_Data/plans.json` (change with the `FalconLoad:DataDirectory` setting). The last 50 runs are kept in memory and can be exported as JSON.
- **Export and import tests** to share them between machines: "Export" on a test (or "Export all" on the dashboard) downloads a JSON file, and "Import…" on another machine loads it. Imported tests get new ids, so an import never overwrites a saved test, and a file is only imported when every test in it is valid. The file includes headers such as `Authorization`, so treat it like the tokens in it.
- **Combine results from several machines**: when one machine can't generate enough load, run the same test on several machines at the same time, export each run, and add the files on the Combine page. It shows the combined throughput, error rate, latency percentiles, status codes and errors, with a per-machine breakdown and a throughput line per machine.
- **JSON API** for scripting and CI.

## JSON API

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/plans` | List saved tests |
| `POST` | `/api/plans` | Create or update a saved test |
| `GET` | `/api/plans/export` | All saved tests as a JSON download |
| `GET` | `/api/plans/{id}/export` | One saved test as a JSON download |
| `POST` | `/api/plans/import` | Save the tests from an exported file (the file is the body) |
| `DELETE` | `/api/plans/{id}` | Delete a saved test |
| `POST` | `/api/plans/{id}/run` | Start a saved test |
| `POST` | `/api/runs` | Start an ad-hoc test (plan in the body, not saved) |
| `GET` | `/api/runs` | List recent runs |
| `GET` | `/api/runs/{id}` | Live or final results |
| `GET` | `/api/runs/{id}/export` | Results as a JSON download, with the latency histograms needed to combine them |
| `POST` | `/api/runs/{id}/stop` | Stop a running test |
| `POST` | `/api/results/combine` | Combine an array of exported results from several machines |
| `POST` | `/api/probe` | Send a single request and return the response |

Example:

```bash
curl -X POST http://localhost:5299/api/runs -H 'content-type: application/json' -d '{
  "name": "Products smoke",
  "requests": [
    { "method": "GET", "url": "https://localhost:5001/api/products?page={{$randomInt:1:20}}" },
    { "method": "POST", "url": "https://localhost:5001/api/orders",
      "headers": [{ "name": "Authorization", "value": "Bearer <token>" }],
      "body": "{ \"id\": \"{{$guid}}\", \"userId\": {{userIds}} }", "expectedStatusCodes": "201" }
  ],
  "parameters": [{ "name": "userIds", "values": ["17", "42", "99"], "selection": "Sequential" }],
  "profile": { "concurrency": 50, "stopCondition": "Duration", "durationSeconds": 60, "rampUpSeconds": 10, "ignoreTlsErrors": true }
}'
```

A spike, held as a request rate of 20 per second with a burst to 200:

```bash
curl -X POST http://localhost:5299/api/runs -H 'content-type: application/json' -d '{
  "name": "Spike",
  "requests": [{ "method": "GET", "url": "https://localhost:5001/api/products" }],
  "profile": {
    "concurrency": 100, "stageMode": "RequestRate",
    "stages": [
      { "durationSeconds": 60, "target": 20 },
      { "durationSeconds": 30, "target": 200 },
      { "durationSeconds": 120, "target": 20 }
    ]
  }
}'
```

## Staged load

A staged profile is a list of stages, each with a duration, a target and a way of getting there: a **hold** jumps to the target and stays, a **ramp** moves in a straight line from where the previous stage ended (the first stage starts from 0). The test lasts as long as the stages add up to, so the stop condition and ramp-up don't apply, and neither does a request count. A target of 0 pauses the load.

- **Virtual users**: the target is how many virtual users are sending requests, each as fast as it can. Users are started as the target rises. When it falls, users above it finish the iteration they are in and stop, so a multi-request scenario is never cut off halfway, and the "Active users" figure follows the target a little late. The rate cap still applies.
- **Requests per second**: the target is a request rate, sent by the "Virtual users" pool of the load profile. Requests are spaced evenly along the stages, so a ramp from 0 spreads its first requests out rather than waiting for a rate that starts at nothing. The rate is a ceiling: when responses are slow and every virtual user is busy, the test sends fewer requests than asked for, and the throughput chart shows the target next to what was sent. Requests that were missed are skipped, never sent later in a burst, so a stage never exceeds its target. Add virtual users if the rate falls short.

Plans saved before stages existed have none and run at a constant load as before. In the JSON, a profile gets `"stageMode": "VirtualUsers"` or `"RequestRate"` and `"stages": [{ "durationSeconds": 60, "target": 50, "ramp": false }]`.

## Project layout

```
src/FalconLoad.Core          Load engine: plan model, runner, metrics (no web dependencies)
src/FalconLoad.Web           ASP.NET Core host, Blazor UI and JSON API
tests/FalconLoad.Core.Tests  xUnit tests for the engine and metrics
```

How the engine works: each virtual user is a task that loops over the plan's requests through a shared `SocketsHttpHandler`. A request is timed until its full response body has been read. Latencies go into a fixed-size log-linear histogram (about 1% precision), so percentiles stay cheap and memory stays flat however long a test runs. Requests still in flight when a test stops are discarded rather than counted as errors.

How combining works: an export holds the run's summary plus its latency histograms (overall, per request and per second), along with the machine's name. Counts are added up, and the histograms are merged bucket by bucket, so the combined percentiles are exactly what one machine would have measured had it sent every request. Each machine's per-second timeline is placed by its start time, so keep the machines' clocks in sync (NTP is enough). Throughput is averaged from the first machine's start to the last one's end; the page warns when runs didn't overlap, because that average then understates the load. Files exported before this feature have no histograms and can't be combined; export the run again if it is still in memory.

## Development

```bash
dotnet build
dotnet test
```

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
- **Response extraction and request chaining**: on a request's Extract tab, take a value from its response into a variable, and use it in later requests as `{{name}}`, like a parameter list. A rule reads a JSON path (`$.data.token`, `items[0].id`, `items[-1].id` for the last item, `['odd key']`), a response header by name (such as `Location`), or a regular expression over the body (its first capture group, or the whole match without one). Each virtual user keeps its own variables across its iterations, so with "Once per virtual user" ticked on a login request, every virtual user logs in once and reuses its token; a request sent once is sent again on the next iteration if it fails, until it succeeds. A response with an expected status that lacks a value counts as an error, such as "Couldn't extract 'token': JSON path $.token found nothing", and the variable keeps its previous value; responses with an unexpected status aren't searched. A variable no response has supplied yet is sent as written. Only the first 1 MB of a body is searched, and extraction happens after the request is timed, so it doesn't add to the latency. "Send once" shows what each rule found and passes the values on to "Send once" on later requests.
- **Load profile**: virtual users (concurrency), stop after a duration or a request count, ramp-up, optional requests-per-second cap, per-request timeout, redirect and TLS options.
- **Success criteria**: expected status codes per request (e.g. `200,201` or `200-299,404`); defaults to any 2xx/3xx.
- **Response checks** per request, on the Checks tab: the body (contains, does not contain, equals, matches a regular expression), a JSON value found by a path such as `$.status`, `$.items[0].id`, `$.items[-1]` or `$.items.length` (exists, equals, contains, matches, is less or greater than a number, and their opposites), or a response header. A response that fails a check counts as an error, named after the check, and the results show how often each check passed. "Send once" shows what each check found. Checks run after the response is timed, so they don't add to its latency. Text comparisons are case-sensitive, and bodies over 8 MB are too large to check, so their body and JSON checks fail.
- **Pass/fail thresholds** for the whole run, such as `p95 latency < 500 ms`, `error rate < 1%` or `throughput > 200 req/s`, for all requests or one of them. A run passes when it completes and every threshold holds; a stopped or failed run doesn't pass. The run page, the runs list and the JSON API show the verdict (`passed`: `true`, `false`, or `null` for a test without thresholds), so a CI job can fail on it. A threshold over requests that sent nothing fails.
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
| `GET` | `/api/runs` | List recent runs, each with its `passed` verdict |
| `GET` | `/api/runs/{id}` | Live or final results, with `results.passed`, `results.thresholds` and `results.checks` |
| `GET` | `/api/runs/{id}/export` | Results as a JSON download, with the latency histograms needed to combine them |
| `POST` | `/api/runs/{id}/stop` | Stop a running test |
| `POST` | `/api/results/combine` | Combine an array of exported results from several machines |
| `POST` | `/api/probe` | Send a single request and return the response, with what its extraction rules found |

Example:

```bash
curl -X POST http://localhost:5299/api/runs -H 'content-type: application/json' -d '{
  "name": "Products smoke",
  "requests": [
    { "method": "POST", "url": "https://localhost:5001/api/login", "body": "{ \"user\": \"load-{{$vu}}\" }",
      "oncePerVirtualUser": true,
      "extractions": [{ "variable": "token", "source": "JsonPath", "expression": "$.accessToken" }] },
    { "method": "GET", "url": "https://localhost:5001/api/products?page={{$randomInt:1:20}}" },
    { "method": "POST", "url": "https://localhost:5001/api/orders",
      "headers": [{ "name": "Authorization", "value": "Bearer {{token}}" }],
      "body": "{ \"id\": \"{{$guid}}\", \"userId\": {{userIds}} }", "expectedStatusCodes": "201",
      "assertions": [
        { "source": "JsonPath", "target": "$.status", "operator": "Equals", "value": "created" },
        { "source": "Header", "target": "Location", "operator": "Exists" }
      ] }
  ],
  "thresholds": [
    { "metric": "P95Latency", "operator": "LessThan", "value": 500 },
    { "metric": "ErrorRate", "operator": "LessThan", "value": 1 },
    { "metric": "P99Latency", "operator": "LessThanOrEqual", "value": 800, "requestIndex": 2 }
  ],
  "parameters": [{ "name": "userIds", "values": ["17", "42", "99"], "selection": "Sequential" }],
  "profile": { "concurrency": 50, "stopCondition": "Duration", "durationSeconds": 60, "rampUpSeconds": 10, "ignoreTlsErrors": true }
}'
```

Check sources are `Body`, `JsonPath` and `Header`; operators are `Contains`, `NotContains`, `Equals`, `NotEquals`, `Matches`, `Exists`, `NotExists`, `LessThan` and `GreaterThan` (a `Body` check takes `Contains`, `NotContains`, `Equals` or `Matches`; `Exists` and `NotExists` need no `value`). Threshold metrics are `ErrorRate` (percent), `MeanLatency`, `P50Latency`, `P90Latency`, `P95Latency`, `P99Latency`, `MaxLatency` (milliseconds) and `RequestsPerSecond`; operators are `LessThan`, `LessThanOrEqual`, `GreaterThan` and `GreaterThanOrEqual`. `requestIndex` is the zero-based position of the request a threshold applies to; leave it out for all requests.

### In CI

Start a saved test, wait for it to finish and fail the job unless it passed its thresholds:

```bash
run=$(curl -fsS -X POST http://localhost:5299/api/plans/$PLAN_ID/run | jq -r .id)
until curl -fsS http://localhost:5299/api/runs/$run | jq -e .results.isFinished > /dev/null; do sleep 2; done
curl -fsS http://localhost:5299/api/runs/$run | jq -e '.results.passed == true' > /dev/null
```

## Project layout

```
src/FalconLoad.Core          Load engine: plan model, runner, metrics (no web dependencies)
src/FalconLoad.Web           ASP.NET Core host, Blazor UI and JSON API
tests/FalconLoad.Core.Tests  xUnit tests for the engine and metrics
```

How the engine works: each virtual user is a task that loops over the plan's requests through a shared `SocketsHttpHandler`, with its own extracted variables. A request is timed until its full response body has been read. Latencies go into a fixed-size log-linear histogram (about 1% precision), so percentiles stay cheap and memory stays flat however long a test runs. Requests still in flight when a test stops are discarded rather than counted as errors.

How combining works: an export holds the run's summary plus its latency histograms (overall, per request and per second), along with the machine's name. Counts are added up, and the histograms are merged bucket by bucket, so the combined percentiles are exactly what one machine would have measured had it sent every request. Each machine's per-second timeline is placed by its start time, so keep the machines' clocks in sync (NTP is enough). Throughput is averaged from the first machine's start to the last one's end; the page warns when runs didn't overlap, because that average then understates the load. Files exported before this feature have no histograms and can't be combined; export the run again if it is still in memory.

## Development

```bash
dotnet build
dotnet test
```

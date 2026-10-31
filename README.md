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
- **Environments and secrets**: define named sets of variables, such as `baseUrl` and `token` for "Staging" and "Production", and write `{{baseUrl}}` or `Bearer {{token}}` in a URL, header value or body. Each run picks the environment, so one test can hit several systems without editing. Mark a variable as a **secret** and its value is hidden in the app, replaced by `******` in error messages and echoed responses, and left out of exports. See [Environments](#environments-and-secret-variables).
- **Load profile**: virtual users (concurrency), stop after a duration or a request count, ramp-up, optional requests-per-second cap, per-request timeout, redirect and TLS options.
- **Success criteria**: expected status codes per request (e.g. `200,201` or `200-299,404`); defaults to any 2xx/3xx.
- **Live results**: a pixel-art falcon flies along the progress bar while a test runs. Requests, throughput, error rate, latency (mean, p50, p90, p95, p99, max), per-second throughput and latency charts, per-request breakdown, status code and error tables.
- **Saved tests** persisted to `App_Data/plans.json` (change with the `FalconLoad:DataDirectory` setting). The last 50 runs are kept in memory and can be exported as JSON.
- **Export and import tests** to share them between machines: "Export" on a test (or "Export all" on the dashboard) downloads a JSON file, and "Import…" on another machine loads it. Imported tests get new ids, so an import never overwrites a saved test, and a file is only imported when every test in it is valid. The file includes any header you typed in directly, such as `Authorization: Bearer abc`, so treat it like the tokens in it, or keep the token in an environment as a secret and write `{{token}}` in the header, which exports as `{{token}}`. The editor warns when a header like `Authorization` holds a plain value.
- **Import requests** from a pasted curl command, a Postman collection (v2.0/v2.1) or an OpenAPI 3 spec (JSON or YAML) on the Import page, then create a new test or add them to an existing one. See [Importing requests](#importing-requests).
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
| `POST` | `/api/import` | Turn a curl command, Postman collection or OpenAPI spec (the body) into a test; see [Importing requests](#importing-requests) |
| `POST` | `/api/import/operations` | List the operations of an OpenAPI spec (the body), so you can choose which to import |
| `DELETE` | `/api/plans/{id}` | Delete a saved test |
| `POST` | `/api/plans/{id}/run` | Start a saved test. `?environment=Name` picks the environment; otherwise the test's own `environment` is used |
| `POST` | `/api/runs` | Start an ad-hoc test (plan in the body, not saved). Takes `?environment=Name` too |
| `GET` | `/api/environments` | List environments. Secret values are `null` |
| `GET` | `/api/environments/{id}` | One environment, secrets hidden |
| `POST` | `/api/environments` | Create or update an environment. A secret sent with `"value": null` keeps its stored value |
| `DELETE` | `/api/environments/{id}` | Delete an environment |
| `GET` | `/api/environments/export` | All environments as a JSON download, without secret values |
| `GET` | `/api/environments/{id}/export` | One environment as a JSON download, without secret values |
| `POST` | `/api/environments/import` | Save the environments from an exported file (the file is the body) |
| `GET` | `/api/runs` | List recent runs |
| `GET` | `/api/runs/{id}` | Live or final results |
| `GET` | `/api/runs/{id}/export` | Results as a JSON download, with the latency histograms needed to combine them |
| `POST` | `/api/runs/{id}/stop` | Stop a running test |
| `POST` | `/api/results/combine` | Combine an array of exported results from several machines |
| `POST` | `/api/probe` | Send a single request and return the response, with what its extraction rules found. Takes `?environment=Name` |

Example:

```bash
curl -X POST 'http://localhost:5299/api/runs?environment=Staging' -H 'content-type: application/json' -d '{
  "name": "Products smoke",
  "requests": [
    { "method": "POST", "url": "{{baseUrl}}/api/login", "body": "{ \"user\": \"load-{{$vu}}\" }",
      "oncePerVirtualUser": true,
      "extractions": [{ "variable": "token", "source": "JsonPath", "expression": "$.accessToken" }] },
    { "method": "GET", "url": "{{baseUrl}}/api/products?page={{$randomInt:1:20}}" },
    { "method": "POST", "url": "{{baseUrl}}/api/orders",
      "headers": [{ "name": "Authorization", "value": "Bearer {{token}}" }],
      "body": "{ \"id\": \"{{$guid}}\", \"userId\": {{userIds}} }", "expectedStatusCodes": "201" }
  ],
  "parameters": [{ "name": "userIds", "values": ["17", "42", "99"], "selection": "Sequential" }],
  "profile": { "concurrency": 50, "stopCondition": "Duration", "durationSeconds": 60, "rampUpSeconds": 10, "ignoreTlsErrors": true }
}'
```

## Importing requests

The **Import** page (and "From curl, Postman or OpenAPI…" on the dashboard) reads text you paste or a file you choose, shows a preview with anything that was left out or still needs attention, and then creates a new test or appends the requests to a saved one. The load profile is left at its defaults for you to set in the editor. Nothing is sent to any URL while importing.

- **curl**: bash-style commands, as copied from API docs or a browser's "Copy as cURL (bash)". Several commands in a row become several requests. Read: the URL, `-X`, `-H`, `-d`/`--data*` (including `--data-urlencode`, `-G` and `--json`), `-F` text fields, `-u`, `-b` with a cookie string, `-A`, `-e`, `-I` and `-k` (which turns on "Ignore TLS errors"). Like curl, a body without a `Content-Type` is sent as `application/x-www-form-urlencoded`. Anything that reads a file (`-d @file`, `-F file=@x`), proxies and client certificates are reported and left out. Windows `cmd` quoting is not understood.
- **Postman**: folders become name prefixes (`Orders / Create order`), and disabled headers stay in the test but switched off. Collection variables become parameter lists, so `{{base-url}}` becomes `{{base_url}}` (list names can't contain dashes or dots), and variables that are used but never defined get an empty list. `{{$guid}}`, `{{$randomUUID}}`, `{{$timestamp}}`, `{{$isoTimestamp}}` and `{{$randomInt}}` map to falcon-load's dynamic values; other Postman dynamic values are sent as written and reported. Raw, urlencoded, form-data (text fields) and GraphQL bodies are converted. Bearer, basic and API key authorization (inherited from folders and the collection) becomes a header. Scripts, environments and file bodies are not read.
- **OpenAPI 3**: the page lists the spec's operations, grouped by tag, with a filter, so you can pick the ones to import. Each request gets a URL on the spec's first server (or the base address you type in), path parameters and required query and header parameters filled from `example`, `default`, the first `enum` value or a sample of the right type, and a JSON, form or text body from the spec's example or generated from its schema. Only `2xx` responses count as success. Bearer, OAuth, basic and API key security becomes a header or query parameter that uses a parameter list named after the security scheme; fill it in before running. Swagger 2.0 files are refused with a hint to convert them. YAML is read by a small built-in reader that handles ordinary spec files but not anchors, aliases, tags or several documents in one file; it says so rather than guessing.

`POST /api/import` takes the text as the body and returns `{ plan, warnings, saved }`. Query parameters: `kind` (`curl`, `postman`, `openapi` or `auto`, the default), `name`, `baseUrl` and repeated `operations` (OpenAPI only; default all), `planId` (append to that saved test instead of creating one), and `save` (default `true`; `false` returns the test without saving it, e.g. to pass it to `POST /api/runs`). Like the other plan endpoints, a test that fails validation comes back as a validation problem.

```bash
curl -X POST 'http://localhost:5299/api/import?operations=GET%20/pets&baseUrl=https://localhost:5001' --data-binary @openapi.yaml
```

## Environments and secret variables

An environment is a named set of variables, kept on the **Environments** page. A request refers to a variable as `{{name}}` (letters, digits and underscores) in its URL, header values or body, and each run chooses which environment supplies the values: in the test editor, the dashboard's per-test picker, `?environment=Name` on the API, or the test's own saved `environment`. A parameter list or a variable extracted from a response with the same name takes priority over an environment variable, and a variable's value may itself use dynamic values such as `{{$guid}}`.

- **Saving and running.** A test can be saved without an environment, even when its URL starts with `{{baseUrl}}`. Running it, or using "Send once", needs the environment, and the error names the variable that has no value. A test refers to its environment by name, so a test imported on another machine finds that machine's environment of the same name. Renaming an environment updates the tests that use it.
- **Secrets are write-only.** After saving, a secret's value is not shown again in the app or returned by the API (it comes back as `null`, meaning "set but hidden"); type a new value to replace it, or send `null` to keep it. The engine replaces secret values with `******` in the errors of a run and in the response a "Send once" shows, in case a server echoes a token back. Values shorter than three characters are not searched for, since they would garble ordinary text.
- **Exports never contain secrets.** The environments file lists the secret's name with no value, so whoever imports it fills the secrets in. A run's export holds the environment's name, not its variables, and a test that uses `{{token}}` exports `{{token}}`. The Combine page warns when the runs used different environments.
- **Where they are stored.** Environments are saved to `environments.json` next to `plans.json`, in plain text like the headers of saved tests, and on Unix only your user can read the file. Don't commit it or copy it between machines; use an export for that.

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

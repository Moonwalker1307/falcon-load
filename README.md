# falcon-load

![falcon-load](docs/logo/falcon-load-logo.png)

An API load tester with a web UI. Configure the HTTP requests you want to hammer, choose how hard and for how long, then watch throughput, latency percentiles, status codes and errors update live.

Built with .NET 10: an ASP.NET Core host with an interactive Blazor Server UI, and a standalone load engine library.

## Run it

```bash
dotnet run --project src/FalconLoad.Web
```

Then open http://localhost:5299 (or https://localhost:7002 with `--launch-profile https`).

### With Docker

```bash
docker build -t falcon-load .
docker run --rm -p 127.0.0.1:5299:8080 -v falcon-load-data:/data falcon-load
```

Then open http://localhost:5299. The app listens on port 8080 inside the container and keeps saved tests in `/data`, so the volume keeps them when the container is replaced. Publishing the port on `127.0.0.1` keeps the app reachable only from your own machine. From inside a container, `localhost` is the container itself: to test an API running on your machine, use `host.docker.internal` (add `--add-host=host.docker.internal:host-gateway` on Linux), or put both containers on the same Docker network and use the API container's name.

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
- **Response checks** per request, on the Checks tab: the body (contains, does not contain, equals, matches a regular expression), a JSON value found by a path such as `$.status`, `$.items[0].id`, `$.items[-1]` or `$.items.length` (exists, equals, contains, matches, is less or greater than a number, and their opposites), or a response header. A response that fails a check counts as an error, named after the check, and the results show how often each check passed. "Send once" shows what each check found. Checks run after the response is timed, so they don't add to its latency. Text comparisons are case-sensitive, and bodies over 8 MB are too large to check, so their body and JSON checks fail.
- **Pass/fail thresholds** for the whole run, such as `p95 latency < 500 ms`, `error rate < 1%` or `throughput > 200 req/s`, for all requests or one of them. A run passes when it completes and every threshold holds; a stopped or failed run doesn't pass. The run page, the runs list and the JSON API show the verdict (`passed`: `true`, `false`, or `null` for a test without thresholds), so a CI job can fail on it. A threshold over requests that sent nothing fails.
- **Live results**: a pixel-art falcon flies along the progress bar while a test runs. Requests, throughput, error rate, latency (mean, p50, p90, p95, p99, max), per-second throughput and latency charts, per-request breakdown, status code and error tables.
- **Saved tests** persisted to `App_Data/plans.json` (change with the `FalconLoad:DataDirectory` setting). The last 50 runs are kept in memory and can be exported as JSON.
- **Run history and baselines**: every finished run of a saved test is stored on disk, so a test's history survives a restart. A test's History page lists its last 30 runs with trend charts. Mark a good run as the **baseline**, and every other run shows its change from it, on the History page, on the run's own page (live while it runs) and on a side-by-side Compare page with both runs' charts overlaid and a per-request breakdown. Changes beyond the regression thresholds are highlighted: red for a regression, green for an improvement. You can also tick any two runs and compare them.
- **Export and import tests** to share them between machines: "Export" on a test (or "Export all" on the dashboard) downloads a JSON file, and "Import…" on another machine loads it. Imported tests get new ids, so an import never overwrites a saved test, and a file is only imported when every test in it is valid. The file includes any header you typed in directly, such as `Authorization: Bearer abc`, so treat it like the tokens in it, or keep the token in an environment as a secret and write `{{token}}` in the header, which exports as `{{token}}`. The editor warns when a header like `Authorization` holds a plain value.
- **Import requests** from a pasted curl command, a Postman collection (v2.0/v2.1) or an OpenAPI 3 spec (JSON or YAML) on the Import page, then create a new test or add them to an existing one. See [Importing requests](#importing-requests).
- **Combine results from several machines**: when one machine can't generate enough load, run the same test on several machines at the same time, export each run, and add the files on the Combine page. It shows the combined throughput, error rate, latency percentiles, status codes and errors, with a per-machine breakdown and a throughput line per machine.
- **Agents**: or let one falcon-load drive the others. Register the other machines on the Agents page, and a test started with "Run on agents" is split between them by weight, started on all of them at the same moment, shown live with their results merged, and stopped on all of them with one button. See [Run a test from several machines](#run-a-test-from-several-machines-agents).
- **Command line runner** that runs exported tests in CI, prints a summary, writes the results as JSON and fails the build when too many requests fail.
- **Docker images** for the web app and the command line runner.
- **JSON API** for scripting and CI.

## Command line

The `falcon-load` CLI runs exported tests without the web UI, for CI pipelines and machines without a browser. Export a test with "Export" in the editor (or "Export all" on the dashboard, or `/api/plans/export`), then:

```bash
dotnet run --project src/FalconLoad.Cli -- run tests.json --out results.json
```

It runs every test in the file in order, shows live progress on standard error, prints a summary of each test (requests, throughput, error rate, latency percentiles, status codes, per-request breakdown and errors) on standard output, and exits with:

| Code | Meaning |
| --- | --- |
| `0` | Every test passed |
| `1` | A test failed: one of its thresholds wasn't met, or, for a test without thresholds, any request failed (or more than `--max-error-rate` allows), or it couldn't run |
| `2` | The command line or the test file is invalid; nothing was sent |
| `130` | Interrupted with Ctrl+C or `docker stop`; the results so far are still printed and written |

| Option | Purpose |
| --- | --- |
| `--test <name>` | Run only the test with this name |
| `--out <path>` | Write the results as JSON, in the same format as "Export" on a run in the UI, so they can be added on the Combine page. With several tests, `<path>` is a directory that gets one file per test |
| `--concurrency <n>` | Use this many virtual users instead of the file's setting |
| `--duration <seconds>` | Run for this long instead of the file's stop condition |
| `--requests <n>` | Send this many requests instead of the file's stop condition |
| `--max-error-rate <percent>` | Fail a test when more than this share of its requests fail, e.g. `1` or `0.5%`. With thresholds this is checked as well as them |
| `--env-file <path>` | An environments file exported from the Environments page, for tests that use `{{variables}}` |
| `--env <name>` | The environment to run with. Default: the test's own environment, or the only one in the file |
| `--var <name>=<value>` | Set a variable, overriding the environment's value. Can be repeated |
| `--machine <name>` | The machine name recorded in the results; useful in containers, whose host names are random |
| `--quiet` | Print only the summary |

A test passes or fails on the same verdict as in the web UI: when it has thresholds, the summary lists each one with its measured value, and the test passes when they all hold. A test without thresholds passes when no request failed.

Environment exports never contain secret values, so give secrets to the CLI as environment variables: `FALCON_LOAD_token` (or `FALCON_LOAD_TOKEN`) sets `{{token}}`. They are masked in the output like secrets in the web app, and a result file records only the environment's name. A value given with `--var` is not treated as a secret, and it is visible to other users of the machine in the process list.

Pass `-` instead of a file name to read the test from standard input. Publish a standalone build with `dotnet publish src/FalconLoad.Cli -c Release -o out`, which gives an `out/falcon-load` executable.

To run the CLI from Docker, mount the folder with the test file on `/work`:

```bash
docker build -f Dockerfile.cli -t falcon-load-cli .
docker run --rm -v "$PWD:/work" --user "$(id -u):$(id -g)" -e FALCON_LOAD_token \
  falcon-load-cli run tests.json --env-file environments.json --out results.json --machine runner-1
```

The `--user` option lets the container write the results file into your folder. Combined with `--machine`, this makes it easy to run the same test from several machines at once and add their result files on the Combine page.

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
| `GET` | `/api/runs` | List recent runs, each with its `passed` verdict |
| `GET` | `/api/runs/{id}` | Live or final results, with `results.passed`, `results.thresholds` and `results.checks` |
| `GET` | `/api/runs/{id}/export` | Results as a JSON download, with the latency histograms needed to combine them |
| `POST` | `/api/runs/{id}/stop` | Stop a running test |
| `GET` | `/api/plans/{id}/history` | A saved test's stored runs, newest first, and its baseline run id |
| `PUT` | `/api/plans/{id}/baseline` | Make a stored run the baseline (`{ "runId": "…" }` in the body) |
| `DELETE` | `/api/plans/{id}/baseline` | Clear the baseline |
| `GET` | `/api/runs/{id}/compare` | Compare a run with the test's baseline, or with `?baseline={runId}`; `hasRegression` is true when anything regressed |
| `POST` | `/api/results/combine` | Combine an array of exported results from several machines |
| `POST` | `/api/probe` | Send a single request and return the response, with what its extraction rules found. Takes `?environment=Name` |
| `POST` | `/api/probe` | Send a single request and return the response |
| `GET` | `/api/agents` | List registered agents with their last check (tokens are never returned) |
| `POST` | `/api/agents` | Register or update an agent (`name`, `url`, `token`, `enabled`, `weight`); an update without a token keeps the saved one |
| `DELETE` | `/api/agents/{id}` | Remove an agent |
| `POST` | `/api/agents/check`, `/api/agents/{id}/check` | Contact every agent, or one, and report reachability, version and clock offset |
| `POST` | `/api/distributed/runs` | Start a test on the agents: `{ "planId": … }` or `{ "plan": … }`, optionally `"agentIds": […]` |
| `GET` | `/api/distributed/runs` | List distributed runs |
| `GET` | `/api/distributed/runs/{id}` | Live or final merged results, with each agent's state |
| `GET` | `/api/distributed/runs/{id}/export` | The merged results plus every agent's export, as a JSON download |
| `POST` | `/api/distributed/runs/{id}/stop` | Stop the test on every agent |
| `GET` | `/api/agent` | *Agent mode.* What this machine says about itself to a controller, including its clock |
| `POST` | `/api/agent/runs` | *Agent mode.* Start a share of a test (`plan`, optional `startAt`, `controller`) |
| `GET` | `/api/agent/runs/{id}` | *Agent mode.* The share's results with histograms, for merging |
| `POST` | `/api/agent/runs/{id}/stop` | *Agent mode.* Stop the share |

The agent-mode endpoints only work when `FalconLoad:AgentToken` is set, and every call needs `Authorization: Bearer <that token>`; see below.

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

The [command line](#command-line) runner is the simplest way: `falcon-load run tests.json` exits non-zero unless every test passes. To use a running web app instead, start a saved test, wait for it to finish and fail the job unless it passed its thresholds:

```bash
run=$(curl -fsS -X POST http://localhost:5299/api/plans/$PLAN_ID/run | jq -r .id)
until curl -fsS http://localhost:5299/api/runs/$run | jq -e .results.isFinished > /dev/null; do sleep 2; done
curl -fsS http://localhost:5299/api/runs/$run | jq -e '.results.passed == true' > /dev/null
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
## Run a test from several machines (agents)

When one machine can't send enough load, falcon-load can run a test from several machines at once and show the result as one. One falcon-load is the controller: the one whose UI you use. The others are agents: ordinary falcon-loads started with agent mode on.

1. **Start an agent** on each extra machine, with a token and an address the controller can reach. By default falcon-load listens on localhost only, so give it the machine's address or all interfaces:

   ```bash
   FalconLoad__AgentToken=<a long random token> dotnet run --project src/FalconLoad.Web --urls http://0.0.0.0:5299
   ```

   Agent mode is off until a token is set, and every request from a controller must carry it as a bearer token (`FalconLoad:AgentToken` in `appsettings.json` works too). Use the plain `http://` address or a certificate the controller trusts, and only do this on a private network: anyone with the token can send load from that machine. The agent's own UI still works, and the shares it runs show up on its Runs page, marked with the controller's name.

   A test that uses an environment runs with the controller's environment: its variables, secrets included, are sent to the agents along with their shares, so the agents need no environments of their own. The agents keep only the environment's name with the run.

2. **Register the agents** on the controller's Agents page: a name, the URL and the token. "Check" contacts the agent and shows its machine name, version, how many tests it is running and how far its clock is from the controller's. Agents are saved in `App_Data/agents.json` next to the saved tests, tokens included, so treat the file like a saved test with an `Authorization` header. Give a bigger machine a higher weight to send it a bigger share.

3. **Run a test on the agents** with "Save & run on agents" in the editor, the "Agents" button next to a saved test, or the Agents page. The controller splits the test by weight: the virtual users, the request count and the rate cap are divided between the enabled agents (every agent used gets at least one virtual user, and a test never uses more agents than it has virtual users), while the duration, ramp-up and timeout apply on each agent as they are. `{{$vu}}` keeps numbering virtual users on from one agent to the next, so every virtual user in the whole test has its own number. A parameter list handed out in order is dealt round-robin to the agents, so no two agents use the same value at the same time and every value is still used once per pass; random and very short lists are sent whole.

The controller first contacts every agent and measures its clock against its own, then tells all of them to start at the same moment a couple of seconds ahead, each on its own clock, so the agents start together even when their clocks differ. If an agent can't be reached or refuses its share, nothing starts, or the agents that did start are stopped again, because part of the load would say little. While the test runs, the controller fetches every agent's results once a second and merges them exactly as the Combine page does, with the histograms, so the live percentiles are those of one test. The run page shows each agent's share and state, the merged numbers and charts with a throughput line per agent, and warnings about clocks that are off by a second or more. "Stop on all agents" stops every share, and the controller waits for their final results. An agent that stops answering is given up on after twenty seconds; its results up to then stay in the total, and the run is reported as failed with the reason. The export of a distributed run holds the merged results and every agent's own export, so the agents' runs can be combined again on the Combine page later. Distributed runs live in memory like other runs and disappear on restart.

## Run history and baselines

Every finished run of a saved test is kept in its history, and one run can be the baseline the others are compared with. To fail a CI build when a run regresses against the baseline, start the saved test, wait for it to finish, then ask for the comparison:

```bash
run=$(curl -s -X POST http://localhost:5299/api/plans/$PLAN_ID/run | jq -r .id)
until curl -s http://localhost:5299/api/runs/$run | jq -e .results.isFinished > /dev/null; do sleep 2; done
curl -s http://localhost:5299/api/runs/$run/compare | jq -e '.hasRegression == false'
```

### Regression thresholds

A run is compared with the baseline metric by metric. A change counts as a regression, or in the other direction an improvement, when it goes beyond these thresholds; smaller changes are treated as noise.

| Setting | Default | Meaning |
| --- | --- | --- |
| `LatencyPercent` | `10` | Mean, p50, p90, p95 or p99 latency rises by more than this percentage of the baseline… |
| `MinLatencyChangeMs` | `1` | …and by at least this many milliseconds, so 0.4 ms to 0.6 ms on a fast endpoint isn't a "50% regression" |
| `ThroughputPercent` | `10` | Average requests per second falls by more than this percentage |
| `ErrorRatePoints` | `1` | The error rate rises by more than this many percentage points (2% to 3.5% is 1.5 points) |

They apply to the whole run and to each request, matched by position in the test. Request counts, bytes and the maximum latency are shown but never judged, because the count follows from the load profile and one slow request moves the maximum. Change the defaults in `appsettings.json` under `FalconLoad:Regression`, or per call with query parameters of the same names in camel case, e.g. `/api/runs/{id}/compare?latencyPercent=5`. The comparison also warns when the load profile or a request's URL changed between the two runs, or when a run was stopped early, since those change what the numbers mean.

How the history is stored: each finished run of a saved test is written whole, as its export file, to `runs/{id}.json` in the data directory, and `runs/index.json` holds the summaries and each test's baseline. A test keeps its newest 30 runs plus its baseline, however old; deleting a test deletes its history. Runs of tests that aren't saved, such as ad-hoc API runs, are not stored. A stored run opens, exports and combines like a run still in memory.

## Project layout

```
src/FalconLoad.Core          Load engine: plan model, runner, metrics (no web dependencies)
src/FalconLoad.Web           ASP.NET Core host, Blazor UI and JSON API
src/FalconLoad.Cli           Headless command line runner (falcon-load run tests.json)
tests/FalconLoad.Core.Tests  xUnit tests for the engine and metrics
tests/FalconLoad.Cli.Tests   xUnit tests for the command line runner
Dockerfile, Dockerfile.cli   Container images for the web app and the CLI
```

How the engine works: each virtual user is a task that loops over the plan's requests through a shared `SocketsHttpHandler`, with its own extracted variables. A request is timed until its full response body has been read. Latencies go into a fixed-size log-linear histogram (about 1% precision), so percentiles stay cheap and memory stays flat however long a test runs. Requests still in flight when a test stops are discarded rather than counted as errors.

How combining works: an export holds the run's summary plus its latency histograms (overall, per request and per second), along with the machine's name. Counts are added up, and the histograms are merged bucket by bucket, so the combined percentiles are exactly what one machine would have measured had it sent every request. Each machine's per-second timeline is placed by its start time, so keep the machines' clocks in sync (NTP is enough). Throughput is averaged from the first machine's start to the last one's end; the page warns when runs didn't overlap, because that average then understates the load. Files exported before this feature have no histograms and can't be combined; export the run again if it is still in memory.

## Development

```bash
dotnet build
dotnet test
```

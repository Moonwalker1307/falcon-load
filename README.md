# falcon-load

![falcon-load](docs/logo/falcon-load-logo.png)

An API load tester with a web UI. Configure the HTTP, GraphQL, WebSocket or gRPC requests you want to hammer, choose how hard and for how long, then watch throughput, latency percentiles, status codes and errors update live.

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
- **GraphQL**: switch a request to GraphQL and write the query, its variables and the operation name instead of a body. A response that reports errors counts as a failed request, even with status 200. See [Protocols](#protocols).
- **WebSocket**: send a message on a connection each virtual user keeps open, and time it until the server's reply arrives. Checks and extractions read the reply, so a subscribe message can feed the messages after it.
- **gRPC**: add the service's `.proto` files, pick a method and write the request message as JSON. Responses are decoded back to JSON for checks and extractions, streaming calls included, and the `grpc-status` decides success. See [Protocols](#protocols).
- **Dynamic values** in URLs, headers and bodies: `{{$guid}}`, `{{$randomInt}}`, `{{$randomInt:1:100}}` (any 64-bit bounds; bounds that aren't whole numbers leave the placeholder as written), `{{$timestamp}}`, `{{$isoTimestamp}}`, `{{$vu}}`, `{{$iteration}}`.
- **Parameter lists**: name a list of values, such as `userIds` = 17, 42, 99, and write `{{userIds}}` in a URL, header value or body. Each iteration of a virtual user takes one value from every list, so all requests in that iteration use the same value. A list hands out its values in order, shared by all virtual users and starting over at the end, or picks one at random. "Send once" uses the first value. Placeholders that match no list are sent as written.
- **Data files**: upload a CSV file or a JSON array of objects, such as a list of test users, and each column becomes a variable (`{{email}}`, `{{password}}`). Every iteration of a virtual user takes one whole row, so an email and its password always belong together. See [Data files](#data-files).
- **Response extraction and request chaining**: on a request's Extract tab, take a value from its response into a variable, and use it in later requests as `{{name}}`, like a parameter list. A rule reads a JSON path (`$.data.token`, `items[0].id`, `items[-1].id` for the last item, `['odd key']`), a response header by name (such as `Location`), or a regular expression over the body (its first capture group, or the whole match without one). Each virtual user keeps its own variables across its iterations, so with "Once per virtual user" ticked on a login request, every virtual user logs in once and reuses its token; a request sent once is sent again on the next iteration if it fails, until it succeeds. A response with an expected status that lacks a value counts as an error, such as "Couldn't extract 'token': JSON path $.token found nothing", and the variable keeps its previous value; responses with an unexpected status aren't searched. A variable no response has supplied yet is sent as written. Only the first 1 MB of a body is searched, and extraction happens after the request is timed, so it doesn't add to the latency. "Send once" shows what each rule found and passes the values on to "Send once" on later requests.
- **Environments and secrets**: define named sets of variables, such as `baseUrl` and `token` for "Staging" and "Production", and write `{{baseUrl}}` or `Bearer {{token}}` in a URL, header value or body. Each run picks the environment, so one test can hit several systems without editing. Mark a variable as a **secret** and its value is hidden in the app, replaced by `******` in error messages and echoed responses, and left out of exports. See [Environments](#environments-and-secret-variables).
- **OAuth2 client credentials**: give a test a token URL, client id and secret, and falcon-load gets a token before the run, sends it as `Authorization: Bearer …` with every request and gets a new one before it expires, so an hour-long soak test doesn't fail when its token runs out. Works the same on agents and in the command line runner. See [OAuth2](#oauth2-client-credentials).
- **Load profile**: virtual users (concurrency), stop after a duration or a request count, ramp-up, optional requests-per-second cap, per-request timeout, redirect and TLS options.
- **Load stages**: instead of a constant load, give the test stages whose targets are virtual users or requests per second. Start from a **step** (climb in equal steps), **spike** (baseline, sudden burst, recovery) or **soak** (ramp up, then hold for a long time) template, or write your own: each stage holds its target or ramps to it in a straight line. A preview chart shows the planned load as you edit, and the run page shows the stage the test is in.
- **Success criteria**: expected status codes per request (e.g. `200,201` or `200-299,404`); defaults to any 2xx/3xx.
- **Response checks** per request, on the Checks tab: the body (contains, does not contain, equals, matches a regular expression), a JSON value found by a path such as `$.status`, `$.items[0].id`, `$.items[-1]` or `$.items.length` (exists, equals, contains, matches, is less or greater than a number, and their opposites), or a response header. A response that fails a check counts as an error, named after the check, and the results show how often each check passed. "Send once" shows what each check found. Checks run after the response is timed, so they don't add to its latency. Text comparisons are case-sensitive, and bodies over 8 MB are too large to check, so their body and JSON checks fail.
- **Pass/fail thresholds** for the whole run, such as `p95 latency < 500 ms`, `error rate < 1%` or `throughput > 200 req/s`, for all requests or one of them. A run passes when it completes and every threshold holds; a stopped or failed run doesn't pass. The run page, the runs list and the JSON API show the verdict (`passed`: `true`, `false`, or `null` for a test without thresholds), so a CI job can fail on it. A threshold over requests that sent nothing fails.
- **Live results**: a pixel-art falcon flies along the progress bar while a test runs. Requests, throughput, error rate, latency (mean, p50, p90, p95, p99, max), per-second throughput and latency charts, per-request breakdown, status code and error tables.
- **Error drill-down**: the run page groups the errors by request, with each call's method and route (the URL's path as written, such as `GET /users/{{id}}`, the operation of a GraphQL request or the method of a gRPC call), and keeps a few failed requests of each as samples: the URL that was called, the status, the error, the request's headers and body and the response's headers and the start of its body. Up to 5 samples are kept per request, the first failures of each kind and at most 2 of the same error, so memory stays flat however many requests fail. Secrets of the environment and the run's OAuth2 tokens are replaced by `******` in every sample, and the values of headers that carry credentials (`Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key` and the like) are never kept. Only the first 2,048 characters of a body are kept; a protobuf or other binary body only shows its size, though a gRPC response that checks decoded shows as JSON. Distributed and combined results keep samples from every machine, labelled with the machine. The JSON API and exports have them as `results.requestErrors` and `results.errorSamples`.
- **Saved tests** persisted to `App_Data/plans.json` (change with the `FalconLoad:DataDirectory` setting). The last 50 runs are kept in memory and can be exported as JSON.
- **Run tags and notes**: label a run with tags such as the build number or commit and a free-text note, when you start it (`?tag=` and `?note=` on the API, `--tag` and `--note` on the CLI) or afterwards on the run's page. The test's History page then filters by tag, so you can look at one build's runs on their own. Tags are kept with the run's export file, so they survive a restart and a combine of exports.
- **Run history and baselines**: every finished run of a saved test is stored on disk, so a test's history survives a restart. A test's History page lists its last 30 runs with trend charts. Mark a good run as the **baseline**, and every other run shows its change from it, on the History page, on the run's own page (live while it runs) and on a side-by-side Compare page with both runs' charts overlaid and a per-request breakdown. Changes beyond the regression thresholds are highlighted: red for a regression, green for an improvement. You can also tick any two runs and compare them.
- **Export and import tests** to share them between machines: "Export" on a test (or "Export all" on the dashboard) downloads a JSON file, and "Import…" on another machine loads it. Imported tests get new ids, so an import never overwrites a saved test, and a file is only imported when every test in it is valid. The file includes any header you typed in directly, such as `Authorization: Bearer abc`, so treat it like the tokens in it, or keep the token in an environment as a secret and write `{{token}}` in the header, which exports as `{{token}}`. The editor warns when a header like `Authorization` holds a plain value.
- **Import requests** from a pasted curl command, a Postman collection (v2.0/v2.1) or an OpenAPI 3 spec (JSON or YAML) on the Import page, then create a new test or add them to an existing one. See [Importing requests](#importing-requests).
- **Combine results from several machines**: when one machine can't generate enough load, run the same test on several machines at the same time, export each run, and add the files on the Combine page. It shows the combined throughput, error rate, latency percentiles, status codes and errors, with a per-machine breakdown and a throughput line per machine.
- **Agents**: or let one falcon-load drive the others. Register the other machines on the Agents page, and a test started with "Run on agents" is split between them by weight, started on all of them at the same moment, shown live with their results merged, and stopped on all of them with one button. See [Run a test from several machines](#run-a-test-from-several-machines-agents).
- **Live metrics in Grafana**: Prometheus scrapes every running test from `/metrics`, and falcon-load can push to InfluxDB and to an OpenTelemetry (OTLP) endpoint while tests run, from the web app and from the command line. A test run on agents is reported by the controller, one series per agent. A ready-made Grafana dashboard and a Docker Compose stack are included. See [Monitoring](#monitoring-with-prometheus-influxdb-and-opentelemetry).
- **Command line runner** that runs exported tests in CI, prints a summary, writes the results as JSON and fails the build when too many requests fail.
- **HTML and JUnit reports** of a finished run, from the run page, the CLI and the API: a self-contained page to read or attach to a CI job, and JUnit XML that CI systems show as test results. See [Reports](#reports).
- **CI integrations**: a GitHub Action and an Azure Pipelines template that run a test, fail the build when it misses its thresholds, publish the reports and post the result as a pull request comment. See [CI integrations](#ci-integrations).
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
| `1` | A test failed: one of its thresholds wasn't met, or, for a test without thresholds, any request failed (or more than `--max-error-rate` allows), or it couldn't run. Also when every test passed but a result file from `--out`, `--html`, `--junit` or `--markdown` couldn't be written, so a pipeline never goes on without its results |
| `2` | The command line or the test file is invalid, including an output path that can't name a file; nothing was sent |
| `130` | Interrupted with Ctrl+C or `docker stop`; the results so far are still printed and written |

| Option | Purpose |
| --- | --- |
| `--test <name>` | Run only the test with this name |
| `--out <path>` | Write the results as JSON, in the same format as "Export" on a run in the UI, so they can be added on the Combine page. With several tests, or when `<path>` ends in `/`, `<path>` is a directory that gets one file per test |
| `--html <path>` | Write an HTML report of each test; see [Reports](#reports). With several tests, or when `<path>` ends in `/`, `<path>` is a directory that gets one file per test |
| `--junit <path>` | Write the results as JUnit XML, one file for all tests, with a test suite per test |
| `--markdown <path>` | Write a short Markdown summary of all tests to one file, for a pull request comment or a CI job's summary page |
| `--concurrency <n>` | Use this many virtual users instead of the file's setting |
| `--duration <seconds>` | Run for this long instead of the file's stop condition |
| `--requests <n>` | Send this many requests instead of the file's stop condition |
| `--max-error-rate <percent>` | Fail a test when more than this share of its requests fail, e.g. `1` or `0.5%`. With thresholds this is checked as well as them |
| `--env-file <path>` | An environments file exported from the Environments page, for tests that use `{{variables}}` |
| `--env <name>` | The environment to run with. Default: the test's own environment, or the only one in the file |
| `--var <name>=<value>` | Set a variable, overriding the environment's value. Can be repeated |
| `--data <name>=<path>` | Use the CSV or JSON file at `<path>` instead of the rows saved in the test's [data file](#data-files) called `<name>`, such as `--data users.csv=./users.csv`. Can be repeated. A name no test uses, or a file that can't be read as a table, exits with `2` |
| `--tag <tag>` | Tag the results, such as the build or commit under test. Can be repeated |
| `--note <text>` | Attach a note to the results |
| `--machine <name>` | The machine name recorded in the results; useful in containers, whose host names are random |
| `--influx <url>` | Write live metrics to InfluxDB at this address, such as `http://localhost:8086`, with the token from the `INFLUX_TOKEN` environment variable. Needs `--influx-bucket` |
| `--influx-bucket <name>`, `--influx-org <name>` | The InfluxDB bucket (the database on InfluxDB 1.8 and 3) and, for InfluxDB 2, the organization |
| `--otlp <endpoint>` | Send live metrics over OTLP/HTTP, such as `http://localhost:4318` or Prometheus's `http://localhost:9090/api/v1/otlp`. Headers come from `OTEL_EXPORTER_OTLP_HEADERS` (`Authorization=Bearer%20abc,X-Scope-OrgID=tenant`) |
| `--metrics-interval <seconds>` | How often `--influx` and `--otlp` push. Default: 5. Each test is pushed once more when it ends |
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

## Reports

A finished run can be written out as an HTML report, as JUnit XML or as a Markdown summary, so a CI job can keep the result and show it.

- **HTML report.** One self-contained page, in the app's colors, with the pass/fail verdict, each threshold with its measured value, throughput, error rate, latency percentiles, throughput and latency charts over time, status codes, the per-request breakdown, response checks, errors by request (method and route) and the sampled failures (when, status and error). It has no scripts and loads nothing from other sites, so it opens from a CI artifact, an email or a file share years later. Print it for a light version.
- **JUnit XML.** Each run is a test suite and each condition of its verdict is a test case: every threshold (named like the threshold, such as `p95 latency < 500 ms`, with the measured value in the failure message), "The run completed", "Requests were sent" and, for a test without thresholds or when `--max-error-rate` is given, the error rate limit. A run that couldn't run at all is reported as an error, and a failed or interrupted one as a failure. The suite's properties carry the headline numbers (requests, throughput, error rate, latency percentiles, the environment's name). With several tests in one CLI run, one file holds a suite for each.
- **Markdown summary** (CLI only, `--markdown`). The verdict, the headline numbers, each condition with its measured value, and the per-request breakdown and most frequent errors in collapsed sections; with several tests, a table of all of them first. It is what the [CI integrations](#ci-integrations) post on a pull request. Text that comes from a test or a server, such as an error message, is escaped, so it can't add links, HTML or @mentions to the comment.

Both reports use the same verdict as the CLI's exit code: a test with thresholds passes when they all hold, one without passes when no request failed, and a run that was stopped early or failed never passes. The web app's badge only appears for tests with thresholds, but a report always states the verdict. The reports contain results only: the test's name, the load profile, the environment's name, step names, errors and numbers. They never contain request headers, bodies, URLs or an environment's values; error text has secrets masked by the engine as everywhere else. Requests appear by their method and route as written, such as `GET /orders/{{id}}`, and the JUnit output and the CLI summary list the most frequent errors with the request that made them.

- **Run page**: the "HTML report" and "JUnit XML" buttons appear when a run has finished; distributed runs have them too.
- **CLI**: `--html <path>` and `--junit <path>`, written even when the build fails, so a pipeline can publish them on failure:

  ```bash
  falcon-load run tests.json --html report.html --junit junit.xml
  ```

  ```yaml
  # GitHub Actions: publish the files whether the test passed or not
  - run: falcon-load run tests.json --html report.html --junit junit.xml
  - uses: actions/upload-artifact@v4
    if: always()
    with: { name: load-test-report, path: report.html }
  ```
- **API**: `GET /api/runs/{id}/report` and `/api/runs/{id}/junit` (and the same under `/api/distributed/runs/{id}`). A run that is still going answers `409`, since its verdict could still change.

## CI integrations

Two ready-made integrations run a test from your repository with the [command line](#command-line) runner, built from the same falcon-load commit you reference, so a pinned version always runs the falcon-load it was written for. Both write the JSON results, an HTML report per test, JUnit XML and the [Markdown summary](#reports); publish them; post the summary on the pull request, updating one comment per job on every push; and then fail the job when a test failed. Point them only at systems you are allowed to load test, such as a staging deployment or a server the job starts itself.

### GitHub Action

```yaml
jobs:
  load-test:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      pull-requests: write   # for the comment
    steps:
      - uses: actions/checkout@v4
      - uses: Moonwalker1307/falcon-load@main   # or a tag or commit
        with:
          file: load-tests/tests.json
          env-file: load-tests/environments.json
          env: Staging
        env:
          FALCON_LOAD_token: ${{ secrets.STAGING_API_TOKEN }}
```

| Input | Purpose |
| --- | --- |
| `file` | The exported test file, relative to `working-directory`. Required |
| `test`, `env-file`, `env`, `concurrency`, `duration`, `requests`, `max-error-rate` | The CLI options of the same name |
| `vars`, `data` | `--var` and `--data`, one `name=value` per line |
| `args` | Any other CLI options, one option and its value per line, such as `--otlp http://collector:4318` |
| `working-directory` | Where relative paths start. Default: the workspace |
| `results-directory` | Where the results are written. Default: `falcon-load-results` |
| `fail-on-error` | `false` to report a failed test without failing the job. Invalid input always fails it. Default: `true` |
| `comment` | `false` to skip the pull request comment. Default: `true` |
| `comment-key` | Jobs with the same key share one comment. Default: the job's id |
| `upload-artifact`, `artifact-name` | Upload the results directory as an artifact. Default: `true`, `falcon-load-results`; give each job its own name when several jobs use the action |
| `github-token` | The token that writes the comment. Default: the job's `GITHUB_TOKEN` |

The outputs are `passed` (`true` or `false`), `exit-code`, `results-directory` and `summary` (the Markdown file's path). The summary also goes on the job's summary page. Secrets go in `FALCON_LOAD_<name>` environment variables, as for the CLI, so they never appear in the workflow's inputs. A pull request from a fork gets a read-only token, so there the comment is skipped with a warning and the result is on the summary page only. The action builds the CLI with the .NET SDK it installs, which takes about half a minute, and runs on Linux, Windows and macOS runners. While this repository is private, other repositories can use the action only after "Access" under Settings, Actions, General allows it.

### Azure Pipelines

The template in `ci/azure-pipelines/falcon-load.yml` checks out falcon-load as a repository resource, then does the same as the action: it publishes the JUnit XML as test results, the results directory as a pipeline artifact and the summary on the run's Extensions tab, and comments on Azure Repos pull requests.

```yaml
resources:
  repositories:
    - repository: falconload          # the name the template checks out
      type: github
      name: Moonwalker1307/falcon-load
      endpoint: my-github-connection   # a GitHub service connection
      ref: refs/heads/main             # or a tag

steps:
  - checkout: self
  - template: ci/azure-pipelines/falcon-load.yml@falconload
    parameters:
      workingDirectory: $(Build.SourcesDirectory)/my-repo
      file: load-tests/tests.json
      envFile: load-tests/environments.json
      env: Staging
      secrets:
        FALCON_LOAD_token: $(stagingApiToken)
```

With two checkouts, Azure Pipelines puts your repository in a folder named after it under `$(Build.SourcesDirectory)`, hence `workingDirectory`. The parameters match the action's inputs in camel case (`envFile`, `maxErrorRate`, `failOnError`, `commentKey`, `resultsDirectory`, `artifactName` and so on), plus `secrets`, a mapping from `FALCON_LOAD_<name>` to secret pipeline variables, because Azure Pipelines hands a secret to a script only when it is mapped. The comment is posted with the job's access token, so the project's build service needs "Contribute to pull requests" on the repository; without it the comment is skipped with a warning. The comment's thread is closed, so it never holds up a policy that requires resolved comments. For a GitHub repository built in Azure Pipelines, the comment is skipped; use the GitHub Action there. The scripts run in PowerShell 7, so the template works on Linux, Windows and macOS agents.

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
| `POST` | `/api/plans/{id}/run` | Start a saved test. `?environment=Name` picks the environment; otherwise the test's own `environment` is used. Repeated `?tag=` and `?note=` label the run |
| `POST` | `/api/runs` | Start an ad-hoc test (plan in the body, not saved). Takes `?environment=Name`, `?tag=` and `?note=` too |
| `GET` | `/api/environments` | List environments. Secret values are `null` |
| `GET` | `/api/environments/{id}` | One environment, secrets hidden |
| `POST` | `/api/environments` | Create or update an environment. A secret sent with `"value": null` keeps its stored value |
| `DELETE` | `/api/environments/{id}` | Delete an environment |
| `GET` | `/api/environments/export` | All environments as a JSON download, without secret values |
| `GET` | `/api/environments/{id}/export` | One environment as a JSON download, without secret values |
| `POST` | `/api/environments/import` | Save the environments from an exported file (the file is the body) |
| `GET` | `/api/runs` | List recent runs, each with its `passed` verdict, `tags` and `note`. Repeated `?tag=` keeps the runs that have every tag |
| `PUT` | `/api/runs/{id}/annotations` | Replace a run's tags and note (`{ "tags": ["build-42"], "note": "…" }`), also after it finished |
| `GET` | `/api/runs/{id}` | Live or final results, with `results.passed`, `results.thresholds`, `results.checks`, `results.requestErrors` and `results.errorSamples` (see [Error drill-down](#features)) and, for a test with OAuth2, `results.auth` |
| `GET` | `/api/runs/{id}/export` | Results as a JSON download, with the latency histograms needed to combine them |
| `GET` | `/api/runs/{id}/report` | A finished run as a self-contained HTML report, as a download |
| `GET` | `/api/runs/{id}/junit` | A finished run as JUnit XML, as a download |
| `POST` | `/api/runs/{id}/stop` | Stop a running test |
| `GET` | `/api/plans/{id}/history` | A saved test's stored runs, newest first, and its baseline run id. Repeated `?tag=` keeps the runs that have every tag |
| `PUT` | `/api/plans/{id}/baseline` | Make a stored run the baseline (`{ "runId": "…" }` in the body) |
| `DELETE` | `/api/plans/{id}/baseline` | Clear the baseline |
| `GET` | `/api/runs/{id}/compare` | Compare a run with the test's baseline, or with `?baseline={runId}`; `hasRegression` is true when anything regressed |
| `POST` | `/api/results/combine` | Combine an array of exported results from several machines |
| `POST` | `/api/probe` | Send a single request and return the response, with what its extraction rules found. Takes `?environment=Name`, and `?planId=` to send it with that saved test's OAuth2 token and proto files (and its environment, unless one is given); a gRPC request needs `planId` for its proto files |
| `GET` | `/api/agents` | List registered agents with their last check (tokens are never returned) |
| `POST` | `/api/agents` | Register or update an agent (`name`, `url`, `token`, `enabled`, `weight`); an update without a token keeps the saved one |
| `DELETE` | `/api/agents/{id}` | Remove an agent |
| `POST` | `/api/agents/check`, `/api/agents/{id}/check` | Contact every agent, or one, and report reachability, version and clock offset |
| `POST` | `/api/distributed/runs` | Start a test on the agents: `{ "planId": … }` or `{ "plan": … }`, optionally `"agentIds": […]` |
| `GET` | `/api/distributed/runs` | List distributed runs |
| `GET` | `/api/distributed/runs/{id}` | Live or final merged results, with each agent's state |
| `GET` | `/api/distributed/runs/{id}/report`, `/api/distributed/runs/{id}/junit` | The merged results of a finished distributed run as an HTML report or JUnit XML |
| `GET` | `/api/distributed/runs/{id}/export` | The merged results plus every agent's export, as a JSON download |
| `POST` | `/api/distributed/runs/{id}/stop` | Stop the test on every agent |
| `GET` | `/metrics` | Prometheus scrape: the running tests and those that finished in the last 5 minutes. `404` when turned off on the Monitoring page |
| `GET` | `/api/telemetry` | Where live metrics go, and how the last push to InfluxDB and the OTLP endpoint went. The token and header values are `null` when set |
| `PUT` | `/api/telemetry` | Change where live metrics go (same shape as the `settings` that `GET` returns). A token or header value sent as `null` keeps the saved one |
| `GET` | `/api/agent` | *Agent mode.* What this machine says about itself to a controller, including its clock |
| `POST` | `/api/agent/runs` | *Agent mode.* Start a share of a test (`plan`, optional `startAt`, `controller`) |
| `GET` | `/api/agent/runs/{id}` | *Agent mode.* The share's results with histograms, for merging |
| `POST` | `/api/agent/runs/{id}/stop` | *Agent mode.* Stop the share |

Bodies are read leniently where that is safe, the same way as imported files: a `null` where the model has no room for one counts as left out, so a text value becomes empty, an object or list keeps its default and a `null` item in a list is dropped, and the usual validation then reports anything that is missing. A `null` for a value that has no default (such as a result's `plan`), a number too large for a double (`1e309`) or a JSON escape that isn't valid UTF-16 (a lone `\ud800`) makes the body invalid JSON, answered with `400`.

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

## Protocols

Every request has a protocol, chosen next to the URL. Plans saved before protocols existed are plain HTTP. A test can mix protocols, so a scenario can log in over HTTP and then talk over a WebSocket or gRPC, and checks, extractions, thresholds, the command line and agents work the same for all of them.

- **HTTP** is the default: method, URL, headers and body.
- **GraphQL** sends an operation to the endpoint URL as an HTTP POST with the JSON body `{ "query": …, "variables": …, "operationName": … }`. The Query tab holds the query, its variables as a JSON object (checked when the test is saved) and the optional operation name; dynamic values and `{{variables}}` work in all three, in a string or as a bare number such as `"first": {{$randomInt:1:50}}`. GraphQL servers answer errors with status 200, so a response whose `errors` array has an entry counts as a failed request, named after the first error's message (`GraphQL error: Not authorised`), as does a response that isn't a JSON object. Checks and extractions read the response as usual: `$.data.products[0].id`. In the JSON, a request gets `"protocol": "GraphQL"`, the query in `body` and `"graphQL": { "variables": "{ … }", "operationName": "…" }`; the `method` is ignored.

```bash
curl -X POST http://localhost:5299/api/runs -H 'content-type: application/json' -d '{
  "name": "Products via GraphQL",
  "requests": [{
    "protocol": "GraphQL", "url": "https://localhost:5001/graphql",
    "body": "query Products($first: Int) { products(first: $first) { id name } }",
    "graphQL": { "variables": "{ \"first\": {{$randomInt:1:50}} }", "operationName": "Products" },
    "assertions": [{ "source": "JsonPath", "target": "$.data.products", "operator": "Exists" }]
  }],
  "profile": { "concurrency": 20, "stopCondition": "Duration", "durationSeconds": 30 }
}'
```

- **WebSocket** sends a text message on a connection to a `ws://` or `wss://` address (`http://` and `https://` work too). Each virtual user opens its own connection the first time a message needs it and keeps it open across its requests and iterations, like a browser tab, so virtual users never share a socket and each one's replies stay in order; "Close the connection afterwards" closes it after the request instead. With **Wait for a reply** (the default) the request is timed from sending until the server's next message has fully arrived, and checks and extractions read that message, as its `Body` or by JSON path. Untick it for a fire-and-forget message, which is done once sent; a reply the server sends anyway is read by the next message that waits. Leave the message empty to only wait for the next message the server pushes. The first message on a connection includes the handshake in its time, like the first HTTP request on a connection does. The request's headers are sent with the handshake, along with the test's OAuth2 token when it has one, and `Sec-WebSocket-Protocol` asks for sub-protocols. A WebSocket request has no status code: the status table counts `Reply` and `Sent` instead, and a connection the server closes, a failed handshake or a reply that doesn't arrive within the timeout counts as an error. Header checks and extractions don't apply. In the JSON, a request gets `"protocol": "WebSocket"`, the message in `body` and `"webSocket": { "waitForReply": true, "closeAfter": false }`.

```bash
curl -X POST http://localhost:5299/api/runs -H 'content-type: application/json' -d '{
  "name": "Order feed",
  "requests": [
    { "protocol": "WebSocket", "url": "wss://localhost:5001/feed", "body": "{ \"subscribe\": \"orders\" }", "oncePerVirtualUser": true,
      "extractions": [{ "variable": "channel", "source": "JsonPath", "expression": "$.channel" }] },
    { "protocol": "WebSocket", "url": "wss://localhost:5001/feed", "body": "{ \"publish\": \"{{channel}}\", \"id\": \"{{$guid}}\" }",
      "assertions": [{ "source": "JsonPath", "target": "$.ack", "operator": "Equals", "value": "true" }] }
  ],
  "profile": { "concurrency": 100, "stopCondition": "Duration", "durationSeconds": 60, "rampUpSeconds": 10 }
}'
```

- **gRPC** calls a method of a gRPC service over HTTP/2. falcon-load reads the service's `.proto` files, which you add to the test under **Proto files** (pasted or uploaded, and saved with the test); name each file as other files import it, such as `shop/v1/shop.proto`, and the usual `google/protobuf/*.proto` imports are built in. The URL is the server's address, such as `https://localhost:5001`; `http://` calls the server over HTTP/2 without TLS, as a local development server usually expects. Pick the method as `package.Service/Method`, and write the request message as JSON in protobuf's JSON form: fields by their `.proto` name or in camelCase, enums by name, 64-bit numbers as numbers or strings, bytes as base64, timestamps as `"2026-10-31T12:00:00Z"`, durations as `"1.5s"` and wrapper types such as `StringValue` as bare values. Dynamic values and `{{variables}}` work in it, and "Fill in the fields" writes a message with every field of the method's input. The message is encoded for every call, so a field name the method doesn't have or a value of the wrong type is an error, caught when the test is saved where it can be. The headers are sent as gRPC metadata, along with the test's OAuth2 token when it has one. The response is decoded to JSON in the same form, so checks and extractions read `$.name`; a server-streaming method is read to the end and checked as a JSON array (`$[0].name`), and a client-streaming method takes a JSON array of messages. Success is decided by the `grpc-status`, not the HTTP status: `OK` by default, or the names or numbers you list under **Expected gRPC status** (`OK,NOT_FOUND`), and any other status counts as an error named with its message (`gRPC NOT_FOUND: No product 0`). The status table counts gRPC status names, and header checks and extractions see the trailers as well as the headers. In the JSON, the test gets `"protoFiles": [{ "name": "…", "content": "…" }]` and a request gets `"protocol": "Grpc"`, the message in `body` and `"grpc": { "method": "package.Service/Method" }`.

```bash
curl -X POST http://localhost:5299/api/runs -H 'content-type: application/json' -d '{
  "name": "Catalog over gRPC",
  "protoFiles": [{ "name": "shop.proto", "content": "syntax = \"proto3\"; package shop.v1; service Catalog { rpc GetProduct (GetProductRequest) returns (Product); } message GetProductRequest { int64 id = 1; } message Product { int64 id = 1; string name = 2; }" }],
  "requests": [{
    "protocol": "Grpc", "url": "https://localhost:5001",
    "grpc": { "method": "shop.v1.Catalog/GetProduct" }, "body": "{ \"id\": {{$randomInt:1:500}} }",
    "assertions": [{ "source": "JsonPath", "target": "$.name", "operator": "Exists" }]
  }],
  "profile": { "concurrency": 50, "stopCondition": "Duration", "durationSeconds": 30 }
}'
```

gRPC calls share the test's HTTP/2 connections, with more connections opened when a server's limit on concurrent streams is reached. Messages are encoded and decoded with the proto files rather than generated code; `google.protobuf.Any` and `Struct` are read and written as their plain message fields.

## Staged load

A staged profile is a list of stages, each with a duration, a target and a way of getting there: a **hold** jumps to the target and stays, a **ramp** moves in a straight line from where the previous stage ended (the first stage starts from 0). The test lasts as long as the stages add up to, so the stop condition and ramp-up don't apply, and neither does a request count. A target of 0 pauses the load.

- **Virtual users**: the target is how many virtual users are sending requests, each as fast as it can. Users are started as the target rises. When it falls, users above it finish the iteration they are in and stop, so a multi-request scenario is never cut off halfway, and the "Active users" figure follows the target a little late. A user that starts again after being stopped begins afresh: values it extracted from earlier responses are gone, and its "once per virtual user" requests are sent again. The rate cap still applies.
- **Requests per second**: the target is a request rate, sent by the "Virtual users" pool of the load profile. Requests are spaced evenly along the stages, so a ramp from 0 spreads its first requests out rather than waiting for a rate that starts at nothing. The rate is a ceiling: when responses are slow and every virtual user is busy, the test sends fewer requests than asked for, and the throughput chart shows the target next to what was sent. Requests that were missed are skipped, never sent later in a burst, so a stage never exceeds its target. Add virtual users if the rate falls short.

On several machines, each agent keeps the stage lengths and gets its weight's share of every target, so the agents together follow the stages. Virtual user targets are rounded up on each agent, which can add up to one user per agent over the target. With the command line, a staged test refuses `--duration` and `--requests`, and `--concurrency` when the stages set virtual users, because the stages decide those.

Plans saved before stages existed have none and run at a constant load as before. In the JSON, a profile gets `"stageMode": "VirtualUsers"` or `"RequestRate"` and `"stages": [{ "durationSeconds": 60, "target": 50, "ramp": false }]`.

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
- **Postman**: folders become name prefixes (`Orders / Create order`), and disabled headers stay in the test but switched off. Collection variables become parameter lists, so `{{base-url}}` becomes `{{base_url}}` (list names can't contain dashes or dots), and variables that are used but never defined get an empty list. `{{$guid}}`, `{{$randomUUID}}`, `{{$timestamp}}`, `{{$isoTimestamp}}` and `{{$randomInt}}` map to falcon-load's dynamic values; other Postman dynamic values are sent as written and reported. Raw, urlencoded and form-data (text fields) bodies are converted, and a GraphQL body becomes a GraphQL request. Bearer, basic and API key authorization (inherited from folders and the collection) becomes a header. Scripts, environments and file bodies are not read.
- **OpenAPI 3**: the page lists the spec's operations, grouped by tag, with a filter, so you can pick the ones to import. Each request gets a URL on the spec's first server (or the base address you type in), path parameters and required query and header parameters filled from `example`, `default`, the first `enum` value or a sample of the right type, and a JSON, form or text body from the spec's example or generated from its schema. Only `2xx` responses count as success. Bearer, OAuth, basic and API key security becomes a header or query parameter that uses a parameter list named after the security scheme; fill it in before running. Swagger 2.0 files are refused with a hint to convert them. YAML is read by a small built-in reader that handles ordinary spec files but not anchors, aliases, tags or several documents in one file; it says so rather than guessing.

`POST /api/import` takes the text as the body and returns `{ plan, warnings, saved }`. Query parameters: `kind` (`curl`, `postman`, `openapi` or `auto`, the default), `name`, `baseUrl` and repeated `operations` (OpenAPI only; default all), `planId` (append to that saved test instead of creating one), and `save` (default `true`; `false` returns the test without saving it, e.g. to pass it to `POST /api/runs`). Like the other plan endpoints, a test that fails validation comes back as a validation problem.

```bash
curl -X POST 'http://localhost:5299/api/import?operations=GET%20/pets&baseUrl=https://localhost:5001' --data-binary @openapi.yaml
```

## Data files

A data file feeds rows of test data into your requests, for example a list of test users to log in with. Add one with "Add file…" in the Data files card of the test editor (it also takes several at once).

```csv
email,password,role
ana@example.test,s3cret-a,admin
bo@example.test,s3cret-b,user
```

```json
[
  { "email": "ana@example.test", "password": "s3cret-a", "role": "admin" },
  { "email": "bo@example.test", "password": "s3cret-b", "role": "user" }
]
```

Each column (CSV) or key (JSON) becomes a variable, written `{{email}}` in a URL, header value or body. Every iteration of a virtual user takes one whole row from each data file, and all the requests of that iteration use the same row. Rows are handed out in order from a counter shared by all virtual users, starting over at the end, or picked at random; choose which in the editor. "Send once" uses the first row. A data file is like a set of parameter lists whose values stay together, and it follows the same rules for names: a column can't have the name of a parameter list, of another data file's column or of a variable extracted from a response, and it takes priority over an environment variable of the same name.

- **CSV.** The first line names the columns, using letters, digits and underscores (a column can't be called `first name`). Values are separated by commas, semicolons or tabs, whichever the first line uses. A value with a separator, a quote or a line break in it goes in double quotes, with `""` for a quote. Spaces around values are kept, blank lines are skipped, and every line must have as many values as the header: a short or long line is an error that names the line, rather than silently shifting values into the wrong column.
- **JSON.** An array of objects. The columns are the keys in the order they first appear, and a row without a key has an empty value there. Strings, numbers and booleans are used as written, `null` is empty, and a nested object or array is used as its JSON text. Comments and trailing commas are accepted.
- **The data is saved with the test.** The file's text is stored in the test (up to 5 MB per file) and included in its export, so an exported test runs on the command line or on an agent with no other files. This also means anyone who can open the test, its export or `GET /api/plans` can read the rows: keep real secrets in an [environment](#environments-and-secret-variables) and use a data file for test data, such as users that exist only in a test system.
- **Results leave the rows out.** A run's export, its stored history and `GET /api/runs/{id}` keep each data file's name and row count but not its rows, so results can be shared and a long history doesn't repeat a large file.
- **Command line.** `--data users.csv=./other-users.csv` runs the test with the rows of that file instead of the ones saved in the test's data file called `users.csv`; the format (CSV or JSON) comes from the file's extension. See [Command line](#command-line).
- **Agents.** Rows handed out in order are dealt round-robin to the agents, like a [parameter list](#run-a-test-from-several-machines-agents), so no two agents send the same row at the same time. Random files, and files with fewer rows than agents, are sent whole. Agents need the same version as the controller; an older agent ignores data files and sends `{{email}}` as written.

## Environments and secret variables

An environment is a named set of variables, kept on the **Environments** page. A request refers to a variable as `{{name}}` (letters, digits and underscores) in its URL, header values or body, and each run chooses which environment supplies the values: in the test editor, the dashboard's per-test picker, `?environment=Name` on the API, or the test's own saved `environment`. A parameter list or a variable extracted from a response with the same name takes priority over an environment variable, and a variable's value may itself use dynamic values such as `{{$guid}}`.

- **Saving and running.** A test can be saved without an environment, even when its URL starts with `{{baseUrl}}`. Running it, or using "Send once", needs the environment, and the error names the variable that has no value. A test refers to its environment by name, so a test imported on another machine finds that machine's environment of the same name. Renaming an environment updates the tests that use it.
- **Secrets are write-only.** After saving, a secret's value is not shown again in the app or returned by the API (it comes back as `null`, meaning "set but hidden"); type a new value to replace it, or send `null` to keep it. The engine replaces secret values with `******` in the errors of a run and in the response a "Send once" shows, in case a server echoes a token back. Values shorter than three characters are not searched for, since they would garble ordinary text.
- **Exports never contain secrets.** The environments file lists the secret's name with no value, so whoever imports it fills the secrets in. A run's export holds the environment's name, not its variables, and a test that uses `{{token}}` exports `{{token}}`. The Combine page warns when the runs used different environments.
- **Where they are stored.** Environments are saved to `environments.json` next to `plans.json`, in plain text like the headers of saved tests, and on Unix only your user can read the file. Don't commit it or copy it between machines; use an export for that.

## OAuth2 client credentials

For an API that takes OAuth2 tokens, set the test's **Authentication** to *OAuth2 client credentials* in the editor and fill in the token URL, client id and secret, and optionally a scope and an audience. "Get a token" asks the token endpoint right away and says how long the token lasts, without showing it.

- **Before the run** falcon-load asks the token endpoint for a token (the `client_credentials` grant, with the client id and secret in a Basic auth header, or in the form body if you choose). It does this before the clock starts, so it doesn't count towards the duration or throughput. A test that can't get a token fails at once with the server's answer, such as `401 Unauthorized: invalid_client`, without sending a request.
- **Every request** gets `Authorization: Bearer <token>`, except a request with its own enabled `Authorization` header, which keeps it. "Send once" fetches a token too. Token requests are not part of the load: they aren't counted as requests, and their time isn't part of any request's latency.
- **During the run** the token is replaced when a quarter of its lifetime (`expires_in`) is left, and at most a minute before it expires. One request to the token endpoint is made for all virtual users, in the background, and requests keep the old token until the new one arrives. If the token endpoint fails, requests carry on with the old token and the request is retried after 1 second, then 2, 4 and so on up to 30. A request whose token has already expired waits for the token request in flight, and goes ahead with the old token if that fails, so the API's answer shows up in the errors. When the API answers 401, the token is replaced on the next request, unless it is less than ten seconds old, since a fresh token being rejected points at a setting rather than an expired token. A token without `expires_in` is kept until the API rejects it.
- **Results** say how many tokens were fetched, how long they lasted and why the last token request failed, on the run page, in the CLI summary and as `results.auth` in the JSON API and exports (`tokensFetched`, `failedFetches`, `lastError`, `tokenLifetimeSeconds`). Combined and distributed results add up every machine's tokens.
- **Keep the secret in an environment.** The auth fields can use the environment's variables, so write `{{clientSecret}}` in the secret field and store the value as a secret variable: it is then left out of exports and masked like any other secret. A secret typed straight into the field is saved and exported with the test, and the editor warns about it. The client secret and every token the run fetched are replaced by `******` in errors and in the response "Send once" shows. The fields can't use parameter lists or extracted variables, because one token serves every virtual user.
- **Agents and the CLI.** Each agent fetches its own tokens with the controller's environment, like the other variables. The command line runner takes the secret from an environment variable, e.g. `FALCON_LOAD_clientSecret=… falcon-load run tests.json`.

In the JSON, a test gets:

```json
"auth": {
  "type": "OAuth2ClientCredentials",
  "tokenUrl": "{{authUrl}}/oauth2/token",
  "clientId": "load-test",
  "clientSecret": "{{clientSecret}}",
  "scope": "orders.read",
  "audience": "",
  "clientAuthentication": "BasicHeader"
}
```

`clientAuthentication` is `BasicHeader` or `RequestBody`. Tests saved before auth existed have no `auth` and send only the headers they were given.
## Run a test from several machines (agents)

When one machine can't send enough load, falcon-load can run a test from several machines at once and show the result as one. One falcon-load is the controller: the one whose UI you use. The others are agents: ordinary falcon-loads started with agent mode on.

1. **Start an agent** on each extra machine, with a token and an address the controller can reach. By default falcon-load listens on localhost only, so give it the machine's address or all interfaces:

   ```bash
   FalconLoad__AgentToken=<a long random token> dotnet run --project src/FalconLoad.Web --urls http://0.0.0.0:5299
   ```

   Agent mode is off until a token is set, and every request from a controller must carry it as a bearer token (`FalconLoad:AgentToken` in `appsettings.json` works too). Use the plain `http://` address or a certificate the controller trusts, and only do this on a private network: anyone with the token can send load from that machine. The agent's own UI still works, and the shares it runs show up on its Runs page, marked with the controller's name.

   A test that uses an environment runs with the controller's environment: its variables, secrets included, are sent to the agents along with their shares, so the agents need no environments of their own. The agents keep only the environment's name with the run.

2. **Register the agents** on the controller's Agents page: a name, the URL and the token. "Check" contacts the agent and shows its machine name, version, how many tests it is running and how far its clock is from the controller's. Agents are saved in `App_Data/agents.json` next to the saved tests, tokens included, so treat the file like a saved test with an `Authorization` header. Give a bigger machine a higher weight to send it a bigger share.

3. **Run a test on the agents** with "Save & run on agents" in the editor, the "Agents" button next to a saved test, or the Agents page. The controller splits the test by weight: the virtual users, the request count and the rate cap are divided between the enabled agents (every agent used gets at least one virtual user, and a test never uses more agents than it has virtual users), while the duration, ramp-up and timeout apply on each agent as they are. `{{$vu}}` keeps numbering virtual users on from one agent to the next, so every virtual user in the whole test has its own number. A parameter list handed out in order is dealt round-robin to the agents, so no two agents use the same value at the same time and every value is still used once per pass; random and very short lists are sent whole. Rows of a [data file](#data-files) are dealt the same way.

The controller first contacts every agent and measures its clock against its own, then tells all of them to start at the same moment a couple of seconds ahead, each on its own clock, so the agents start together even when their clocks differ. If an agent can't be reached or refuses its share, nothing starts, or the agents that did start are stopped again, because part of the load would say little. While the test runs, the controller fetches every agent's results once a second and merges them exactly as the Combine page does, with the histograms, so the live percentiles are those of one test. The run page shows each agent's share and state, the merged numbers and charts with a throughput line per agent, and warnings about clocks that are off by a second or more. "Stop on all agents" stops every share, and the controller waits for their final results. An agent that stops answering is given up on after twenty seconds; its results up to then stay in the total, and the run is reported as failed with the reason. The export of a distributed run holds the merged results and every agent's own export, so the agents' runs can be combined again on the Combine page later. Distributed runs live in memory like other runs and disappear on restart.

## Monitoring with Prometheus, InfluxDB and OpenTelemetry

The **Monitoring** page sends the live numbers of every test to the tools you already watch your systems with, so a load test shows up in Grafana next to the CPU, queue lengths and error logs of the API it hits. There are three ways out, and any of them can be used together:

- **Prometheus** scrapes `http://<falcon-load>/metrics`. It is on by default (turn it off on the Monitoring page) and shows the tests that are running and those that finished in the last five minutes. Scrape every few seconds for live charts:

  ```yaml
  scrape_configs:
    - job_name: falcon-load
      scrape_interval: 5s
      static_configs:
        - targets: ["localhost:5299"]
  ```

- **InfluxDB**: give the URL, bucket, organization and token. falcon-load writes through `/api/v2/write`, which InfluxDB 2 and 3 have, and 1.8 too with the database as the bucket and `user:password` as the token.
- **OpenTelemetry**: an OTLP/HTTP endpoint, such as an OpenTelemetry Collector or Grafana Alloy on `http://localhost:4318`, a hosted service, or Prometheus 3 itself started with `--web.enable-otlp-receiver` (endpoint `http://prometheus:9090/api/v1/otlp`). `/v1/metrics` is added to the address unless it ends with it already. Add headers such as `Authorization` for a hosted endpoint. Metrics are sent as OTLP JSON.

InfluxDB and the OTLP endpoint are pushed to every 5 seconds while a test runs (change the interval on the page), and once more when it ends, so even a test shorter than the interval arrives with its final numbers. The page shows whether the last push worked, and why not when it didn't, such as `InfluxDB answered 401 Unauthorized`; a failing destination never affects the test. The InfluxDB token and header values are write-only, like the secrets of an environment: they are saved in `telemetry.json` next to the saved tests, readable only by your user on Unix, and never shown again or returned by the API.

**What is sent.** Every series is labelled with `run_id`, `test` and `machine`, and the per-request ones with `request` (its name, or method and URL as written, so `{{token}}` stays `{{token}}`) and `request_index`. Error messages and response bodies are never sent.

| Prometheus name (OTLP name) | Type | Meaning |
| --- | --- | --- |
| `falcon_load_requests_total` (`falcon_load_requests`) | counter | Requests by `request` and `outcome` (`success` or `failure`, where a failure is a transport error, an unexpected status or a failed check) |
| `falcon_load_request_duration_seconds` (`falcon_load_request_duration`, unit `s`) | histogram | Latency until the full response body was read, by `request`, in buckets from 1 ms to 60 s |
| `falcon_load_responses_total` (`falcon_load_responses`) | counter | Responses by `status_code`, `none` when no response arrived |
| `falcon_load_checks_total` (`falcon_load_checks`) | counter | Response checks by `request`, `check` and `outcome` |
| `falcon_load_virtual_users` | gauge | Virtual users sending requests |
| `falcon_load_sent_bytes_total`, `falcon_load_received_bytes_total` (`falcon_load_sent`, `falcon_load_received`, unit `By`) | counter | Bytes sent and received |
| `falcon_load_run_active` | gauge | 1 while the run is going, 0 once it has finished (labelled with `run_id` and `test` only) |
| `falcon_load_run_passed` | gauge | 1 while the run's thresholds hold, 0 when one fails or the run was stopped; only for tests with thresholds |

The OTLP names become the Prometheus names when Prometheus receives them over OTLP, so the same queries and dashboard work either way. Counters and histograms are cumulative from the start of their run. Throughput is `sum(rate(falcon_load_requests_total[30s]))`, and the p95 latency over any window is `histogram_quantile(0.95, sum by (le) (rate(falcon_load_request_duration_seconds_bucket[30s])))`, within about 1% of a bucket bound.

InfluxDB has no histogram a dashboard can add up, so each push writes ready-made numbers for the interval since the previous one: the measurements `falcon_load` (the whole run), `falcon_load_request` (each request, tag `request`), `falcon_load_machine` (each machine or agent, tag `machine`) and `falcon_load_status` (field `count` per `status_code`). The first three have the running totals `requests` and `failures`, and for the interval `rps`, `interval_requests`, `interval_failures`, `error_percent` and `latency_mean_ms`, `latency_p50_ms`, `latency_p90_ms`, `latency_p95_ms`, `latency_p99_ms` and `latency_max_ms` (left out when the interval had no requests). `falcon_load` also has `virtual_users`, `bytes_sent`, `bytes_received`, `running` and, with thresholds, `passed`. The percentiles come from the difference between two pushes' histograms, with the agents merged first, so they are exact for the whole test.

**Tests on agents.** The controller reports a test it runs on agents, with one series per agent (`machine` is the agent's name), so `sum` adds them up to the whole test and `histogram_quantile` over the summed buckets gives its percentiles. Agents leave the shares they run for a controller out of their own `/metrics` and pushes, so scraping every machine never counts a request twice; configure the destinations on the controller.

**Grafana.** [`docs/monitoring`](docs/monitoring) has a dashboard for Prometheus, [`falcon-load.json`](docs/monitoring/grafana/falcon-load.json), with throughput, error rate, latency percentiles, status codes and virtual users, per machine and per request, and pickers for the test, run and machine. Import it in Grafana (Dashboards, New, Import) and choose your Prometheus data source. Or start falcon-load, Prometheus and Grafana together, already connected, with:

```bash
cd docs/monitoring
docker compose up --build
```

Then open falcon-load on http://localhost:5299, run a test, and watch it in Grafana on http://localhost:3000. Prometheus in that stack also accepts OTLP on http://localhost:9090/api/v1/otlp, for the command line runner:

```bash
falcon-load run tests.json --otlp http://localhost:9090/api/v1/otlp
```

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

Tags and notes: a run can have up to 10 tags of at most 64 characters (no commas) and a note of up to 2,000 characters. Tags are compared ignoring case, and filtering by several tags keeps the runs that have all of them. They are stored in the run's file and in the index like the rest of the summary, and `PUT /api/runs/{id}/annotations` rewrites both. Reports don't show them, and runs from before this feature simply have none. In CI, `curl -X POST "http://localhost:5299/api/plans/$id/run?tag=build-$BUILD&tag=$GIT_SHA"` ties a run to the commit it tested.

## Project layout

```
src/FalconLoad.Core          Load engine: plan model, runner, metrics, reports (no web dependencies)
src/FalconLoad.Web           ASP.NET Core host, Blazor UI and JSON API
src/FalconLoad.Cli           Headless command line runner (falcon-load run tests.json)
tests/FalconLoad.Core.Tests  xUnit tests for the engine and metrics
tests/FalconLoad.Cli.Tests   xUnit tests for the command line runner
tests/FalconLoad.Fuzz        A fuzzer for every input parser (plan files, imports, data files, templates, .proto, CLI arguments)
Dockerfile, Dockerfile.cli   Container images for the web app and the CLI
action.yml, ci/              The GitHub Action and the Azure Pipelines template
docs/monitoring              Grafana dashboard and a Docker Compose stack with Prometheus and Grafana
```

How the engine works: each virtual user is a task that loops over the plan's requests through a shared `SocketsHttpHandler`, with its own extracted variables and its own WebSocket connections. A request is timed until its full response body has been read. Latencies go into a fixed-size log-linear histogram (about 1% precision), so percentiles stay cheap and memory stays flat however long a test runs. Requests still in flight when a test stops are discarded rather than counted as errors. Error samples cost nothing until a request fails: the start of a body is only kept for a response whose status already makes it a failure, or when its checks read the body anyway, and once a request's samples are taken, checking for room is a single read.

How combining works: an export holds the run's summary plus its latency histograms (overall, per request and per second), along with the machine's name. Counts are added up, and the histograms are merged bucket by bucket, so the combined percentiles are exactly what one machine would have measured had it sent every request. Each machine's per-second timeline is placed by its start time, so keep the machines' clocks in sync (NTP is enough). Throughput is averaged from the first machine's start to the last one's end; the page warns when runs didn't overlap, because that average then understates the load. Files exported before this feature have no histograms and can't be combined; export the run again if it is still in memory.

## Development

```bash
dotnet build
dotnet test
```

The parsers that read user input (test and environment files, curl, Postman and OpenAPI imports, YAML, CSV and JSON data files, templates, JSONPath, checks, `.proto` files, protobuf messages, CLI arguments, result exports and monitoring settings) have a fuzzer, which feeds them mutated inputs and reports any exception their callers don't expect. Run it after changing a parser; see [tests/FalconLoad.Fuzz/README.md](tests/FalconLoad.Fuzz/README.md):

```bash
dotnet run --project tests/FalconLoad.Fuzz -c Release -- --seconds 20
```

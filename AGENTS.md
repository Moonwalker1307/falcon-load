# AGENTS.md

Guidance for coding agents (and people) working in this repository. `CLAUDE.md` imports this file, so Claude Code and
other agents read the same rules.

falcon-load is an API load tester with a web UI, built on .NET 10. You configure the HTTP requests to send, choose how
hard and for how long, and watch throughput, latency percentiles, status codes and errors update live. It has two
parts: a load engine library with no web dependencies (`src/FalconLoad.Core`), and an ASP.NET Core host with an
interactive Blazor Server UI and a JSON API over it (`src/FalconLoad.Web`).

## Read first

Before non-trivial work, read `README.md`: what the tool does, the dynamic values, the JSON API table and how the
engine works. It is the only user-facing documentation, so it has to stay true.

## Core rules

- **The engine stays independent of the web host.** `src/FalconLoad.Core` references nothing from ASP.NET Core or
  Blazor and no third-party package. Anything a load test needs (plan model, runner, metrics, templating, probing)
  lives there, so it can be tested on its own and reused from a CLI or CI later. `src/FalconLoad.Web` references the
  engine, never the other way round.
- **No new packages without a reason.** The UI is plain Blazor and CSS; the charts are hand-written SVG
  (`Components/Shared/LineChart.razor`). Don't add a JavaScript chart library, a CSS framework or a component library.
  The test project uses xUnit v2, so xUnit v3 APIs such as `TestContext` are not available.
- **Measurements must stay honest.** A request is timed until its full response body has been read. Latencies go into
  `LatencyHistogram`, a fixed-size log-linear histogram, so memory stays flat however long a test runs; never keep a
  list of every sample. Requests still in flight when a test stops are discarded, not counted as errors. Any change to
  what is counted or how it is timed needs a test and a line in the README.
- **Keep the hot path lean.** Each virtual user is a task looping over `PreparedRequest`s through one shared
  `SocketsHttpHandler`. Work that doesn't change between requests is done once in `PreparedRequest.From`, and
  `TemplateRenderer` skips text without variables. Don't allocate, log or take locks per request beyond what
  `MetricsCollector.Record` already does.
- **Thread safety.** `RunManager` and `PlanStore` are singletons shared by every Blazor circuit and the API.
  `LoadTestRunner` runs a cloned plan, so an edit in the UI never changes a running test. Events raised from
  background work (`LoadTestRun.Updated`, `RunManager.RunsChanged`, `PlanStore.Changed`) reach components through
  `InvokeAsync`, and every component that subscribes unsubscribes in `Dispose`.
- **Plans are user data.** `App_Data/plans.json` (or the `FalconLoad:DataDirectory` setting) holds saved tests,
  including their headers and tokens. A model change must still load files written by the previous version, and
  validation messages come from `Validate()` on the model, shared by the UI and the API.
- **Secrets stay secret.** `environments.json` holds environments with their secret values. A secret leaves
  `EnvironmentStore` only through `TestEnvironment.Redacted()` (UI, API and exports) or to the engine, and the engine
  hides secret values in error text with `SecretMasker`. A new place that shows, logs or exports data from a run or an
  environment needs a test that a secret doesn't come out.
- **Only test what you are allowed to.** falcon-load sends real traffic to any URL it is given. Tests use
  `FakeHandler` and never hit the network. Don't point it at third-party services while developing, and don't add
  features meant to expose it publicly (authentication bypasses, open CORS, binding to all interfaces by default).
- **Never name a user's private application or employer** in code, tests, commits, the README or PR descriptions. Use
  neutral names such as "MyApp" or "a production API".

## Repo map

- `src/FalconLoad.Core/`
  - `Model/`: `LoadTestPlan` (requests plus profile, `Validate()`, `Clone()`), `DataFile` (CSV or JSON rows kept in the
    plan; its columns become `{{column}}` variables through `ParameterSet`), `TestEnvironment` and `SecretMasker`
    (variable sets and hiding their secrets), `RequestDefinition`, `LoadProfile`
    (concurrency, stop condition, ramp-up, rate cap, timeout, TLS and redirect options, and optional `LoadStage`s that
    `StageSchedule` turns into load over time), `StagePresets` (step, spike and soak), `AuthSettings` (OAuth2 client
    credentials), `StatusCodeMatcher` (`200,201` or `200-299,404`) and `TemplateRenderer` (the `{{$guid}}`-style
    dynamic values).
  - `Engine/`: `LoadTestRunner` runs a plan once and reports snapshots; `PreparedRequest` precomputes each request;
    `HttpHandlerFactory` builds the shared handler; `OAuth2TokenSource` fetches a run's tokens and replaces them before
    they expire; `RequestProbe` sends one request for the "Send once" button and
    `/api/probe`.
  - `Reports/`: `RunVerdict` (the one pass/fail verdict behind the CLI's exit code and both reports), `HtmlReport`
    (a self-contained page with hand-written SVG charts, no script) and `JUnitReport`. Reports show results only,
    never headers, bodies or environment values.
  - `Metrics/`: `MetricsCollector` aggregates `RequestResult`s into a `LoadTestSnapshot`, overall, per request and per
    second, using `LatencyHistogram`.
- `src/FalconLoad.Web/`
  - `Program.cs`: service registration and the middleware pipeline.
  - `Services/`: `RunManager` (in-memory runs, the last 50 finished ones are kept), `PlanStore` (saved plans in JSON),
    `EnvironmentStore` (environments in JSON),
    `ApiEndpoints` (the `/api` minimal API group).
  - `Components/Pages/`: `Home` (dashboard), `PlanEditor`, `Environments` and `EnvironmentEditor`, `RunView` (live results), `Runs`.
    `Components/Shared/`: charts, the runs table, badges and `Format` helpers. `wwwroot/app.css` holds all styling.
- `src/FalconLoad.Cli/`: the headless `falcon-load` command. `CliOptions` parses the arguments by hand (no package),
  `CliApp` loads a `PlanFile`, runs each plan with `LoadTestRunner` and writes a `RunExport`, and `Summary` formats
  the text output. Exit codes are part of its contract with CI scripts: 0 passed, 1 failed, 2 invalid input, 130
  interrupted.
- `tests/FalconLoad.Core.Tests/`: xUnit tests of the runner and metrics. `FakeHandler` stands in for the network.
- `tests/FalconLoad.Cli.Tests/`: xUnit tests of the CLI, sharing `FakeHandler` with the engine tests.
- `Dockerfile` (web app) and `Dockerfile.cli`: multi-stage images built from the repository root. A new project the
  web app or the CLI references must also be copied in its Dockerfile.
- `.github/workflows/ci.yml`: restore, build and test in Release on Ubuntu for every push to `main` and every PR, and
  build both Docker images.

## Build and verify

```
dotnet build -c Release
dotnet test -c Release --no-build
dotnet test --filter "FullyQualifiedName~MetricsTests"   # one class
dotnet run --project src/FalconLoad.Web                  # http://localhost:5299
dotnet run --project src/FalconLoad.Cli -- run tests.json  # headless run of an exported file
```

Before pushing, the build must be free of new warnings and the tests must pass. For a UI change, run the app and try it
in a browser against a real run; a load test against the app's own `/api/plans` endpoint is a handy local target.

In a cloud container, the .NET SDK may be missing. Install it with `apt-get install dotnet-sdk-10.0`, because the
dotnet-install script's download host may be blocked. Don't stop the app with `pkill -f FalconLoad.Web`: the pattern
also matches the shell that runs the command. Kill it by its process id instead.

## Change-specific guidance

- **A new dynamic value**: add it to `TemplateRenderer` and its doc comment, a test, the README list and the variables table in
  the plan editor.
- **A new load profile option**: add it to `LoadProfile` with a default that keeps old saved plans working, validate it
  in `Validate()`, honour it in the runner or `HttpHandlerFactory`, show it in `PlanEditor` and describe it in the
  README.
- **A new metric**: compute it in `MetricsCollector` from data it already has, add it to `LoadTestSnapshot`, test it in
  `MetricsTests`, and show it in `RunView`. The JSON export and `/api/runs/{id}` return the snapshot, so a renamed or
  removed field breaks scripts that read it.
- **A new or changed API endpoint**: keep it in the `/api` group in `ApiEndpoints`, return
  `Results.ValidationProblem` for invalid plans as the existing endpoints do, and update the README table.
- **A new CLI option**: parse it in `CliOptions`, add it to the usage text, test it in `CliAppTests` and add it to the
  README's option table.
- **UI work**: follow the existing pages: plain HTML with the classes in `app.css`, no inline styles where a class
  exists, and components that dispose their event subscriptions.

## Avoid

- Referencing ASP.NET Core, Blazor or any package from `src/FalconLoad.Core`.
- Tests that need the network, real time measured with tight bounds, or `Thread.Sleep`. Use `FakeHandler` and count
  requests instead of timing them.
- Skipping, disabling or loosening a test to get CI green.
- Storing every latency sample, or anything else whose memory grows with test length.
- Calling `StateHasChanged` from a background thread without `InvokeAsync`.
- Breaking the format of saved plans or the JSON API without a migration and a README note.

## Working mindset

- Read the code around a change first and match it: naming, comment density, file layout, error handling.
- Prefer the smallest change that fixes the cause, and keep a pull request to one change.
- When a number in the UI changes, check it against a known load: a fake handler in a test, or a local endpoint with a
  request count stop condition.
- Write down what you checked: the tests run, the run you watched in the browser.

## Commit guidance

- Branch from the latest `origin/main`, one branch per piece of work.
- Name the branch after the work it carries, in lowercase words joined by hyphens, with a `claude/` prefix when an
  agent creates it (e.g. `claude/csv-export`). Never use a generic or generated name such as
  `claude/project-thread-xxxx`, even when a tool suggests one. Don't rename a branch that already has an open pull
  request.
- Never reuse a branch name. Once a pull request is merged or closed, its branch is finished: follow-up work goes on a
  new branch with a new name, even when it continues the same feature.
- Commit subjects are short imperative or descriptive sentences without prefixes, e.g. "Add a requests-per-second cap".
- The body explains why.
- Fill in `.github/pull_request_template.md`; CI must be green before review.

## Wisdom

- Code comments and docs are plain, full sentences explaining why, not what.
- The rate cap hands out time slots from a shared counter, so the cap holds across all virtual users rather than per
  user.
- Ramp-up starts virtual users spread over the ramp-up period; with a request-count stop condition, the count is shared
  by all of them, so a test sends exactly that many requests.
- Cloud VMs and CI runners vary a lot in CPU and network, so a throughput number from one machine says little about
  another. Compare before and after on the same machine.
- Runs live only in memory and disappear on restart; saved plans are the only state on disk.

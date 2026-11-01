# falcon-load fuzzer

A small mutational fuzzer for the places where falcon-load reads input it didn't write: test and environment files,
curl, Postman and OpenAPI imports, YAML, CSV and JSON data files, templates, JSONPath, checks and extractions, `.proto`
files and protobuf messages, GraphQL bodies, CLI arguments, result exports and monitoring settings. It has no
dependencies, so it builds with the rest of the solution, and it never touches the network: requests go to a fake server
in the process that answers with random statuses, bodies and delays.

It isn't a test project, so `dotnet test` doesn't run it. CI runs a short round with a fixed seed.

## Running it

```bash
dotnet run --project tests/FalconLoad.Fuzz -c Release -- --seconds 20            # every target, 20 s each
dotnet run --project tests/FalconLoad.Fuzz -c Release -- --target yaml,import     # some targets
dotnet run --project tests/FalconLoad.Fuzz -c Release -- --iterations 2000 --seed 1  # the same inputs every time
dotnet run --project tests/FalconLoad.Fuzz -c Release -- --list                   # what each target covers
```

A round prints its seed, so `--seed` repeats it. It exits with 0 when nothing was found, 1 when there are findings and
3 when an input ran for over 30 seconds.

## How it works

Each target is a function from a string to a short description of what happened. The fuzzer starts from the target's
seeds (realistic inputs, in `Seeds.cs`), mutates them as text and, when the input is JSON, as a JSON tree: values
swapped for nulls, huge numbers, other types and escapes such as a lone `\ud800`, keys and items removed or repeated.
An input that leads to a new description joins the corpus, so the fuzzer works its way into the parsers.

A finding is an exception the target doesn't list as expected (the exceptions a parser documents for bad input, such
as `ImportException`, are fine), a broken promise checked by the target (for example, an export must import again, a
run must never send more requests than its count, and no secret may appear in a snapshot, report or CLI output), or an
input that takes over 3 seconds. Findings are grouped by exception type and the first frame in falcon-load's code, so
one bug is reported once.

## Findings

Each finding is saved to `--out` (default `fuzz-findings`): the input that found it, a minimized input (`.min`) that
still fails the same way, and a `.log` with the exception. The `.txt` files are for reading; the `.input` files keep
characters a UTF-8 file can't hold. Repeat one with:

```bash
dotnet run --project tests/FalconLoad.Fuzz -c Release -- --target yaml --replay fuzz-findings/yaml-1.min.input
```

Fix the cause in the parser, then add the minimized input as a test in `tests/FalconLoad.Core.Tests` (most are in
`FuzzRegressionTests`), so the bug can't come back.

## Adding a target

Add a `FuzzTarget` to `Targets.cs` with a name, seeds that the code accepts (`--check-seeds` shows the problems of the
seed plans) and the exceptions the surface promises its callers. Throw `InvariantException` when a result breaks a rule
the code promises. Keep the target fast: the fuzzer is only useful when it runs thousands of inputs a second.

The web app's JSON API binds bodies with the same options as plan files (`JsonInput`), so the `plan-file`,
`environment-file`, `import`, `results` and `telemetry` targets cover its parsing too. `--write-seeds <dir>` writes every
seed to a folder, for fuzzing a locally running app over HTTP with another tool.

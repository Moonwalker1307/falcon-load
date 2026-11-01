using System.Diagnostics;
using System.Globalization;
using FalconLoad.Fuzz;

// falcon-load's fuzzer. See README.md in this folder for how to run it and what a finding means.
const string usage = """
    Usage: dotnet run --project tests/FalconLoad.Fuzz -c Release -- [options]

      --target <name,...>   Fuzz only these targets. Default: all of them.
      --seconds <n>         Fuzz each target for this long. Default: 30.
      --iterations <n>      Stop each target after this many inputs instead (deterministic with --seed).
      --seed <n>            The random seed. Default: a new one each run, printed so a round can be repeated.
      --out <dir>           Where findings are saved. Default: fuzz-findings.
      --replay <file>       Run one saved input through --target and print what happens.
      --list                List the targets.
      --write-seeds <dir>   Write every target's seed inputs to a folder, for fuzzing the web API from outside.

    Exits with 0 when nothing was found, 1 when there are findings and 3 when an input hung.
    """;

var targets = Targets.All;
string[]? only = null;
var seconds = 30.0;
long iterations = long.MaxValue;
var seed = Random.Shared.Next();
var output = Path.GetFullPath("fuzz-findings");
string? replay = null;
for (var i = 0; i < args.Length; i++)
{
    string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
    switch (args[i])
    {
        case "--target":
            only = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            break;
        case "--seconds":
            seconds = double.Parse(Value(), CultureInfo.InvariantCulture);
            break;
        case "--iterations":
            iterations = long.Parse(Value(), CultureInfo.InvariantCulture);
            break;
        case "--seed":
            seed = int.Parse(Value(), CultureInfo.InvariantCulture);
            break;
        case "--out":
            output = Path.GetFullPath(Value());
            break;
        case "--replay":
            replay = Value();
            break;
        case "--check-seeds":
            foreach (var plan in new[] { Seeds.RichPlan(), Seeds.StagedPlan(), Seeds.VirtualUserStages() })
            {
                Console.WriteLine($"{plan.Name}: {string.Join(" | ", plan.Validate())}");
            }

            return 0;
        case "--write-seeds":
            var folder = Directory.CreateDirectory(Value()).FullName;
            foreach (var t in targets)
            {
                for (var n = 0; n < t.Seeds.Count; n++)
                {
                    Fuzzer.Save(Path.Combine(folder, $"{t.Name}-{n}"), t.Seeds[n]);
                }
            }

            return 0;
        case "--list":
            foreach (var t in targets)
            {
                Console.WriteLine($"{t.Name,-18} {t.Description}");
            }

            return 0;
        default:
            Console.Error.WriteLine(usage);
            return 2;
    }
}

var selected = only is null ? targets : [.. targets.Where(t => only.Contains(t.Name))];
if (selected.Count == 0 || only is not null && selected.Count != only.Length)
{
    Console.Error.WriteLine($"Unknown target. Known: {string.Join(", ", targets.Select(t => t.Name))}");
    return 2;
}

// Everything the CLI target writes lands in a sandbox, never next to the sources.
var replayPath = replay is null ? null : Path.GetFullPath(replay);
Environment.CurrentDirectory = Targets.SandboxDirectory;

if (replayPath is not null)
{
    Console.WriteLine(Fuzzer.Replay(selected[0], Fuzzer.Load(replayPath)));
    return 0;
}

Fuzzer.StartWatchdog(output, TimeSpan.FromSeconds(30));
Console.WriteLine($"Fuzzing {selected.Count} target(s) with seed {seed}; findings go to {output}.");
var total = new List<Finding>();
var clock = Stopwatch.StartNew();
foreach (var target in selected)
{
    var fuzzer = new Fuzzer(target, seed, output, Console.Out);
    var started = clock.Elapsed;
    var limit = iterations == long.MaxValue ? long.MaxValue : target.Slow ? Math.Max(1, iterations / 10) : iterations;
    fuzzer.Run(limit, iterations == long.MaxValue ? TimeSpan.FromSeconds(seconds) : TimeSpan.MaxValue);
    Console.WriteLine($"{target.Name,-18} {fuzzer.Executions,9:N0} inputs in {(clock.Elapsed - started).TotalSeconds,5:0.0} s, {fuzzer.Findings.Count} finding(s)");
    total.AddRange(fuzzer.Findings);
}

Console.WriteLine(total.Count == 0 ? "No findings." : $"{total.Count} finding(s):");
foreach (var finding in total)
{
    Console.WriteLine($"  {finding.Target}: {finding.Bucket}");
}

return total.Count == 0 ? 0 : 1;

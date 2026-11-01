using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FalconLoad.Fuzz;

/// <summary>Raised by a target when a result breaks a rule the code promises, such as an export that can't be imported again.</summary>
internal sealed class InvariantException(string message) : Exception(message);

/// <summary>
/// One input surface. <see cref="Run"/> feeds it an input and returns a short description of the outcome, which the
/// fuzzer uses to keep inputs that reached somewhere new. An exception other than one of <see cref="Expected"/> is a
/// finding.
/// </summary>
internal sealed class FuzzTarget
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required Func<string, string> Run { get; init; }

    public required IReadOnlyList<string> Seeds { get; init; }

    public IReadOnlyList<string> Dictionary { get; init; } = [];

    /// <summary>Exceptions the surface documents for bad input; its callers catch them.</summary>
    public IReadOnlyList<Type> Expected { get; init; } = [];

    /// <summary>Targets that run a load test are slower, so a round gives them fewer inputs.</summary>
    public bool Slow { get; init; }
}

internal sealed record Finding(string Target, string Bucket, string Input, string Minimized, string Details);

internal sealed partial class Fuzzer(FuzzTarget target, int seed, string outputDirectory, TextWriter log)
{
    private const int MaxCorpus = 4000;

    // An input that takes longer than this is reported, since a parser should never take seconds over a pasted file.
    private static readonly TimeSpan SlowInput = TimeSpan.FromSeconds(3);

    // A target that runs a load test waits on fake requests that time out, so it gets longer.
    private static readonly TimeSpan SlowRun = TimeSpan.FromSeconds(20);

    private static long _startedAt;
    private static volatile string? _current;
    private static volatile string? _currentTarget;

    private readonly Dictionary<string, Finding> _findings = [];
    private readonly HashSet<string> _outcomes = [];

    public IReadOnlyCollection<Finding> Findings => _findings.Values;

    public long Executions { get; private set; }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    /// <summary>
    /// Watches for an input that never finishes. It can't be stopped from inside the process, so the watchdog saves it
    /// and exits, and the next round starts again without it once it is fixed.
    /// </summary>
    public static void StartWatchdog(string outputDirectory, TimeSpan limit)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(1000);
                var started = Interlocked.Read(ref _startedAt);
                if (_current is { } input && started != 0 && Stopwatch.GetElapsedTime(started) > limit)
                {
                    Directory.CreateDirectory(outputDirectory);
                    var path = Path.Combine(outputDirectory, $"{_currentTarget}-hang.txt");
                    Save(path[..^4], input);
                    Console.Error.WriteLine($"HANG in {_currentTarget}: an input ran for over {limit.TotalSeconds:0} s. Saved to {path}.");
                    Environment.Exit(3);
                }
            }
        })
        { IsBackground = true, Name = "fuzz watchdog" };
        thread.Start();
    }

    public void Run(long iterations, TimeSpan duration)
    {
        var random = new Random(seed);
        var corpus = new List<string>(target.Seeds);
        var mutator = new Mutator(random, target.Dictionary);
        var clock = Stopwatch.StartNew();

        // Seeds must run cleanly: a seed that fails means the harness, not the code, is wrong.
        foreach (var input in target.Seeds)
        {
            Execute(input, out _);
        }

        while (Executions < iterations && clock.Elapsed < duration)
        {
            var parent = corpus[random.Next(corpus.Count)];
            var input = mutator.Mutate(parent, corpus);
            var outcome = Execute(input, out var interesting);
            if (interesting && _outcomes.Add(outcome) && corpus.Count < MaxCorpus)
            {
                corpus.Add(input);
            }
        }
    }

    /// <summary>Runs one input; saves it when it is a finding.</summary>
    private string Execute(string input, out bool interesting)
    {
        Executions++;
        var (outcome, error, elapsed) = Invoke(input);
        interesting = true;
        if (error is not null)
        {
            Record(input, error, Bucket(error));
            interesting = false;
            return "crash";
        }

        if (elapsed > (target.Slow ? SlowRun : SlowInput))
        {
            Record(input, $"Took {elapsed.TotalSeconds:0.0} s.", "slow");
        }

        return outcome;
    }

    private (string Outcome, string? Error, TimeSpan Elapsed) Invoke(string input)
    {
        _currentTarget = target.Name;
        _current = input;
        Interlocked.Exchange(ref _startedAt, Stopwatch.GetTimestamp());
        try
        {
            var outcome = target.Run(input);
            return (outcome, null, Stopwatch.GetElapsedTime(Interlocked.Read(ref _startedAt)));
        }
        catch (Exception ex) when (Allowed(ex))
        {
            return ($"{ex.GetType().Name}: {Digits().Replace(ex.Message.Length > 60 ? ex.Message[..60] : ex.Message, "#")}", null, Stopwatch.GetElapsedTime(Interlocked.Read(ref _startedAt)));
        }
        catch (Exception ex)
        {
            return ("crash", ex.ToString(), TimeSpan.Zero);
        }
        finally
        {
            _current = null;
            Interlocked.Exchange(ref _startedAt, 0);
        }
    }

    private bool Allowed(Exception ex) => target.Expected.Any(t => t.IsInstanceOfType(ex));

    /// <summary>
    /// Groups crashes by the exception and the first frame in falcon-load's own code, so the same bug found by many
    /// inputs is reported once. An invariant failure is grouped by its message.
    /// </summary>
    private static string Bucket(string error)
    {
        var lines = error.Split('\n');
        var first = lines[0].Trim();
        var type = first.Split(':')[0];
        if (type.EndsWith(nameof(InvariantException), StringComparison.Ordinal))
        {
            var message = Digits().Replace(first[(type.Length + 1)..].Trim(), "#");
            return "invariant: " + (message.Length > 100 ? message[..100] : message);
        }

        var frame = lines.Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("at FalconLoad.", StringComparison.Ordinal) && !l.StartsWith("at FalconLoad.Fuzz.", StringComparison.Ordinal))
            ?? lines.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at ", StringComparison.Ordinal)) ?? "?";
        var paren = frame.IndexOf('(');
        return $"{type} {(paren > 0 ? frame[..paren] : frame)}";
    }

    private void Record(string input, string details, string bucket)
    {
        if (_findings.ContainsKey(bucket))
        {
            return;
        }

        var minimized = bucket == "slow" ? input : Minimize(input, bucket);
        var finding = new Finding(target.Name, bucket, input, minimized, details);
        _findings[bucket] = finding;
        Directory.CreateDirectory(outputDirectory);
        var name = $"{target.Name}-{_findings.Count}";
        Save(Path.Combine(outputDirectory, name), input);
        Save(Path.Combine(outputDirectory, name + ".min"), minimized);
        File.WriteAllText(Path.Combine(outputDirectory, name + ".log"), $"{bucket}\n\n{details}\n", new UTF8Encoding(false, throwOnInvalidBytes: false));
        log.WriteLine($"  NEW {target.Name}: {bucket}");
        log.WriteLine($"      minimized ({minimized.Length} chars): {Preview(minimized)}");
    }

    /// <summary>
    /// Saves an input twice: as text to read, and escaped to <c>.input</c>, which keeps characters such as lone
    /// surrogates that a UTF-8 file can't hold. <c>--replay</c> reads either.
    /// </summary>
    public static void Save(string pathWithoutExtension, string input)
    {
        File.WriteAllText(pathWithoutExtension + ".txt", input, new UTF8Encoding(false, throwOnInvalidBytes: false));
        var escaped = new StringBuilder();
        foreach (var c in input)
        {
            if (c is '\\')
            {
                escaped.Append("\\\\");
            }
            else if (c is >= ' ' and < (char)0x7f or '\n')
            {
                escaped.Append(c);
            }
            else
            {
                escaped.Append($"\\u{(int)c:x4}");
            }
        }

        File.WriteAllText(pathWithoutExtension + ".input", escaped.ToString());
    }

    /// <summary>Reads an input saved by <see cref="Save"/>.</summary>
    public static string Load(string path)
    {
        var text = File.ReadAllText(path);
        if (!path.EndsWith(".input", StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '\\')
            {
                result.Append('\\');
                i++;
            }
            else if (text[i] == '\\' && i + 5 < text.Length && text[i + 1] == 'u')
            {
                result.Append((char)Convert.ToInt32(text.Substring(i + 2, 4), 16));
                i += 5;
            }
            else
            {
                result.Append(text[i]);
            }
        }

        return result.ToString();
    }

    /// <summary>Removes pieces of the input while it still fails the same way, so a regression test can use a short one.</summary>
    private string Minimize(string input, string bucket)
    {
        var best = input;
        var attempts = 0;

        // An input of a slow target takes up to seconds, so it gets fewer attempts and the round goes on.
        var limit = target.Slow ? 60 : 3000;
        for (var chunk = Math.Max(1, best.Length / 2); chunk >= 1 && attempts < limit; chunk /= 2)
        {
            for (var start = 0; start < best.Length && attempts < limit;)
            {
                var candidate = Mutator.Clean(best.Remove(start, Math.Min(chunk, best.Length - start)));
                attempts++;
                var (_, error, _) = Invoke(candidate);
                if (error is not null && Bucket(error) == bucket)
                {
                    best = candidate;
                }
                else
                {
                    start += chunk;
                }
            }
        }

        return best;
    }

    private static string Preview(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Length > 300 ? text[..300] : text)
        {
            sb.Append(c switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' or (>= '\ud800' and <= '\udfff') => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return text.Length > 300 ? sb + "\u2026" : sb.ToString();
    }

    public static string Replay(FuzzTarget target, string input)
    {
        try
        {
            return "outcome: " + target.Run(input);
        }
        catch (Exception ex) when (target.Expected.Any(t => t.IsInstanceOfType(ex)))
        {
            return $"expected {ex.GetType().Name}: {ex.Message}";
        }
        catch (Exception ex)
        {
            return "FINDING: " + ex;
        }
    }
}

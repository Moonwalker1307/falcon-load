using System.Collections.Concurrent;

namespace FalconLoad.Core.Metrics;

/// <summary>
/// Keeps a few failed requests of each request of the plan as examples. The first failures of each kind are kept, at
/// most <see cref="MaxPerError"/> of one error and <see cref="MaxPerRequest"/> in all, so a request that fails in
/// several ways shows each of them and memory stays flat however many requests fail.
/// </summary>
/// <remarks>
/// Building a sample costs more than recording a result, so a virtual user asks <see cref="TryReserve"/> first and
/// only builds one when a slot is free. Once a request's slots are taken, the check is a single read without a lock.
/// </remarks>
public sealed class ErrorSampler
{
    public const int MaxPerRequest = 5;
    public const int MaxPerError = 2;

    private readonly Lock _gate = new();
    private readonly int[] _taken;
    private readonly ConcurrentDictionary<(int Step, string Error), int> _perError = new();
    private readonly List<ErrorSample> _samples = [];

    public ErrorSampler(int requests)
    {
        _taken = new int[requests];
    }

    /// <summary>Whether a failure of request <paramref name="step"/> could still be kept, so its body is worth reading.</summary>
    public bool Wants(int step) => Volatile.Read(ref _taken[step]) < MaxPerRequest;

    /// <summary>Takes a slot for a failure of request <paramref name="step"/> with <paramref name="error"/>; false when it isn't wanted.</summary>
    public bool TryReserve(int step, string error)
    {
        if (!Wants(step) || _perError.TryGetValue((step, error), out var same) && same >= MaxPerError)
        {
            return false;
        }

        lock (_gate)
        {
            same = _perError.GetValueOrDefault((step, error));
            if (_taken[step] >= MaxPerRequest || same >= MaxPerError)
            {
                return false;
            }

            _perError[(step, error)] = same + 1;
            Volatile.Write(ref _taken[step], _taken[step] + 1);
            return true;
        }
    }

    /// <summary>Adds a sample whose slot <see cref="TryReserve"/> handed out.</summary>
    public void Add(ErrorSample sample)
    {
        lock (_gate)
        {
            _samples.Add(sample);
        }
    }

    /// <summary>The samples so far, by request and then in the order they were taken.</summary>
    public IReadOnlyList<ErrorSample> Samples()
    {
        lock (_gate)
        {
            return _samples.OrderBy(s => s.StepIndex).ThenBy(s => s.AtSeconds).ToList();
        }
    }
}

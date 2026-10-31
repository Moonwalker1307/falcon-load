using FalconLoad.Core.Model;

namespace FalconLoad.Core.Engine;

/// <summary>
/// Keeps a response body for its checks and extraction rules. Each virtual user owns one and reuses it for every response, so keeping
/// bodies only allocates while the buffer grows to the largest body seen.
/// </summary>
internal sealed class ResponseBuffer
{
    private byte[] _bytes = new byte[16 * 1024];
    private int _limit = Assertion.MaxBodyBytes;

    public int Length { get; private set; }

    /// <summary>
    /// Set when the body outgrew the limit given to <see cref="Reset"/>. Only its start was kept: enough for extraction,
    /// which only reads the start, but not for checks, which would judge a body they haven't seen.
    /// </summary>
    public bool TooLarge { get; private set; }

    public ReadOnlyMemory<byte> Memory => _bytes.AsMemory(0, Length);

    /// <summary>Empties the buffer for the next body, of which it keeps at most <paramref name="limit"/> bytes.</summary>
    public void Reset(int limit)
    {
        _limit = limit;
        Length = 0;
        TooLarge = false;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (TooLarge)
        {
            return;
        }

        var needed = Length + data.Length;
        if (needed > _limit)
        {
            TooLarge = true;
            data = data[..(_limit - Length)];
            needed = _limit;
        }

        if (needed > _bytes.Length)
        {
            Array.Resize(ref _bytes, Math.Min(_limit, Math.Max(needed, _bytes.Length * 2)));
        }

        data.CopyTo(_bytes.AsSpan(Length));
        Length = needed;
    }
}

using FalconLoad.Core.Model;

namespace FalconLoad.Core.Engine;

/// <summary>
/// Keeps a response body for its checks. Each virtual user owns one and reuses it for every response, so keeping
/// bodies only allocates while the buffer grows to the largest body seen.
/// </summary>
internal sealed class ResponseBuffer
{
    private byte[] _bytes = new byte[16 * 1024];

    public int Length { get; private set; }

    /// <summary>Set when the body outgrew <see cref="Assertion.MaxBodyBytes"/>; the rest of it was not kept.</summary>
    public bool TooLarge { get; private set; }

    public ReadOnlyMemory<byte> Memory => TooLarge ? ReadOnlyMemory<byte>.Empty : _bytes.AsMemory(0, Length);

    public void Reset()
    {
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
        if (needed > Assertion.MaxBodyBytes)
        {
            TooLarge = true;
            return;
        }

        if (needed > _bytes.Length)
        {
            Array.Resize(ref _bytes, Math.Min(Assertion.MaxBodyBytes, Math.Max(needed, _bytes.Length * 2)));
        }

        data.CopyTo(_bytes.AsSpan(Length));
        Length = needed;
    }
}

using System.Buffers;
using System.Runtime.CompilerServices;

namespace Highway.Server.Storage.Layout;

/// <summary>
/// A ref struct that writes key bytes into a caller-supplied span, growing to a
/// pooled array only when the stack budget is exceeded. Every key builder writes
/// through it — the single allocation control point for the keyspace.
///
/// <para>Ported from <c>stow-rocksdb</c> (see
/// <c>docs/features/037-rocksdb-engine/reference/stow-engine/Encoding/KeyWriter.cs</c>),
/// unchanged but for the namespace. It is Layer A — the physical KV mechanics — which
/// feature 037 adopts whole (see <c>physical-layout.md</c> §1).</para>
/// </summary>
internal ref struct KeyWriter
{
    private Span<byte> _buffer;
    private byte[]? _pooledArray;
    private int _position;

    /// <summary>Initialises the writer over a caller-supplied span (typically <c>stackalloc</c>).</summary>
    public KeyWriter(Span<byte> initialBuffer)
    {
        _buffer = initialBuffer;
        _pooledArray = null;
        _position = 0;
    }

    /// <summary>Number of bytes written so far.</summary>
    public readonly int Length => _position;

    /// <summary>The written bytes as a read-only span.</summary>
    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer[.._position];

    /// <summary>Writes a single byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    /// <summary>Writes a span of bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBytes(scoped ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_buffer[_position..]);
        _position += bytes.Length;
    }

    /// <summary>Copies the written bytes to a new array and returns any pooled buffer. Call when done.</summary>
    public byte[] ToArray()
    {
        var result = _buffer[.._position].ToArray();
        Return();
        return result;
    }

    /// <summary>Returns any pooled array without copying. Use when the written span was already consumed.</summary>
    public void Return()
    {
        if (_pooledArray != null)
        {
            ArrayPool<byte>.Shared.Return(_pooledArray);
            _pooledArray = null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int needed)
    {
        var newSize = Math.Max(_buffer.Length * 2, _position + needed);
        var newArray = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer[.._position].CopyTo(newArray);

        if (_pooledArray != null)
            ArrayPool<byte>.Shared.Return(_pooledArray);

        _pooledArray = newArray;
        _buffer = newArray.AsSpan();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int needed)
    {
        if (_position + needed > _buffer.Length)
            Grow(needed);
    }
}

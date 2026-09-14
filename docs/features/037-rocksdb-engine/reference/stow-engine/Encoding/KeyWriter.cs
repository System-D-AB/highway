using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Stow.Storage.Encoding;

/// <summary>
/// A ref struct that writes key bytes into a caller-supplied Span, growing
/// to a pooled array only when the stack budget is exceeded.
/// Every encoder writes through this — it is the single allocation control point.
/// </summary>
public ref struct KeyWriter
{
    private Span<byte> _buffer;
    private byte[] _pooledArray;
    private int _position;

    /// <summary>
    /// Initialises the writer over a caller-supplied span (typically stackalloc).
    /// </summary>
    public KeyWriter(Span<byte> initialBuffer)
    {
        _buffer = initialBuffer;
        _pooledArray = null;
        _position = 0;
    }

    /// <summary>
    /// Number of bytes written so far.
    /// </summary>
    public int Length => _position;

    /// <summary>
    /// Returns the written bytes as a ReadOnlySpan.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.Slice(0, _position);

    /// <summary>
    /// Writes a single byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    /// <summary>
    /// Writes a span of bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBytes(scoped ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_buffer.Slice(_position));
        _position += bytes.Length;
    }

    /// <summary>
    /// Copies the written bytes to a new byte array and returns the pooled
    /// buffer (if any) to the pool. Must be called when done.
    /// </summary>
    public byte[] ToArray()
    {
        var result = _buffer.Slice(0, _position).ToArray();
        Return();
        return result;
    }

    /// <summary>
    /// Returns the pooled array (if any) without copying. Call this if you
    /// already consumed the WrittenSpan.
    /// </summary>
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
        int newSize = Math.Max(_buffer.Length * 2, _position + needed);
        var newArray = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.Slice(0, _position).CopyTo(newArray);

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

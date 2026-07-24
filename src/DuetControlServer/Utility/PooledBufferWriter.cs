using System;
using System.Buffers;

namespace DuetControlServer.Utility;

/// <summary>
/// Buffer writer over ArrayPool-rented memory. Unlike ArrayBufferWriter, large buffers are
/// recycled across instances, so short-lived writers (e.g. one IPC connection per REST query)
/// do not allocate a fresh LOH array each time the serialized payload exceeds 85 KB
/// </summary>
/// <param name="initialCapacity">Initial buffer capacity</param>
public sealed class PooledBufferWriter(int initialCapacity) : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    private int _written;

    /// <summary>
    /// Memory written so far
    /// </summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <summary>
    /// Number of bytes written so far
    /// </summary>
    public int WrittenCount => _written;

    /// <summary>
    /// Size of the underlying buffer
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Reset the writer keeping the underlying buffer
    /// </summary>
    public void Reset() => _written = 0;

    /// <inheritdoc />
    public void Advance(int count) => _written += count;

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 1)
        {
            sizeHint = 1;
        }

        if (_buffer.Length - _written < sizeHint)
        {
            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(_written + sizeHint, _buffer.Length * 2));
            Array.Copy(_buffer, newBuffer, _written);
            ReturnBuffer();
            _buffer = newBuffer;
        }
    }

    /// <summary>
    /// Return the underlying buffer to the pool. The writer must not be used afterwards
    /// </summary>
    public void Dispose()
    {
        ReturnBuffer();
        _written = 0;
    }

    /// <summary>
    /// Return the current buffer to the pool unless it has already been returned
    /// </summary>
    /// <remarks>
    /// ArrayPool buckets a zero-length array as the smallest size class, so returning the empty
    /// array left behind by a previous call would put a foreign array into the pool
    /// </remarks>
    private void ReturnBuffer()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }
}

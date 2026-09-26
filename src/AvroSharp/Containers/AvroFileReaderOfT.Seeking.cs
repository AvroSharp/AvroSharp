using System;

namespace AvroSharp.Containers;

/// <summary>
/// Seeking and splitting, on seekable streams, following the Java implementation: <see cref="PreviousSync"/>,
/// <see cref="Seek"/>, <see cref="Sync"/> and <see cref="PastSync"/>.
/// </summary>
public sealed partial class AvroFileReader<T>
{
    /// <summary>
    /// Gets the stream position where the block of the most recently read object starts (just after the previous
    /// sync marker); <see cref="Seek"/> returns to it. Before the first object, the first block's position.
    /// </summary>
    /// <exception cref="NotSupportedException">The stream cannot seek.</exception>
    public long PreviousSync
    {
        get
        {
            RequireSeekable();
            return _blockStart >= 0 ? _blockStart : _firstBlockStart;
        }
    }

    /// <summary>Moves to a block start, a position <see cref="PreviousSync"/> returned; the next object read is that block's first.</summary>
    /// <param name="position">The stream position of a block start.</param>
    /// <exception cref="NotSupportedException">The stream cannot seek.</exception>
    public void Seek(long position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireSeekable();
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        _stream.Position = position;
        _inputStart = 0;
        _inputEnd = 0;
        _objectsLeft = 0;
        _blockData = default;
        _blockStart = position;
    }

    /// <summary>
    /// Moves to the block after the first sync marker that starts at or after <paramref name="position"/>, found by
    /// scanning. A position at or before the header's own marker moves to the first block, even when the header's
    /// metadata happens to contain the marker. With no marker after the position, the reader is at the end.
    /// </summary>
    /// <param name="position">Any stream position, for example the start of a split.</param>
    /// <exception cref="NotSupportedException">The stream cannot seek.</exception>
    public void Sync(long position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireSeekable();
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        // The header's own marker ends at the first block; a split starting at or before it begins there. Scanning
        // from inside the header could match the marker in metadata (Apache's syncInMeta.avro), so it is not scanned.
        if (position <= _firstBlockStart - AvroContainerFormat.SyncSize)
        {
            Seek(_firstBlockStart);
            return;
        }

        Seek(position);
        while (true)
        {
            var found = _input.AsSpan(_inputStart, Buffered).IndexOf(_sync);
            if (found >= 0)
            {
                _inputStart += found + AvroContainerFormat.SyncSize;
                _blockStart = LogicalPosition;
                return;
            }

            // Keep a partial marker at the end of the buffer, then read more.
            _inputStart = Math.Max(_inputStart, _inputEnd - (AvroContainerFormat.SyncSize - 1));
            if (!FillAtLeast(Buffered + 1))
            {
                _inputStart = _inputEnd;
                _blockStart = LogicalPosition;
                return;
            }
        }
    }

    /// <summary>
    /// Gets whether the block of the most recently read object starts past a split that ends at
    /// <paramref name="position"/>, so the object belongs to the next split. Read a split as:
    /// <c>reader.Sync(start); while (reader.TryRead(out var value) &amp;&amp; !reader.PastSync(end)) { ... }</c>.
    /// </summary>
    /// <param name="position">The end of the split.</param>
    /// <exception cref="NotSupportedException">The stream cannot seek.</exception>
    public bool PastSync(long position)
    {
        RequireSeekable();
        return PreviousSync >= position + AvroContainerFormat.SyncSize || PreviousSync >= _stream.Length;
    }

    private long LogicalPosition => _stream.Position - Buffered;

    private void MarkFirstBlock()
    {
        if (_stream.CanSeek)
        {
            _firstBlockStart = LogicalPosition;
        }
    }

    private void MarkBlockStart()
    {
        if (_stream.CanSeek && _objectsLeft == 0)
        {
            _blockStart = LogicalPosition;
        }
    }

    private void RequireSeekable()
    {
        if (!_stream.CanSeek)
        {
            throw new NotSupportedException("Seeking needs a stream that can seek.");
        }
    }
}

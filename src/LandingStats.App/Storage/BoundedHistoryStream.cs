using System;
using System.IO;

namespace LandingStats.App.Storage;

internal sealed class BoundedHistoryStream : Stream
{
    internal const long MaximumDetailBytes = 512L * 1024 * 1024;
    internal const long MaximumIndexBytes = 64L * 1024 * 1024;
    private readonly Stream _source;
    private readonly long _maximum;
    private long _read;
    public BoundedHistoryStream(Stream source, long maximum) { _source = source; _maximum = maximum; }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var allowed = (int)Math.Min(Math.Min(count, 65536), _maximum - _read + 1);
        if (allowed <= 0) throw new InvalidDataException("Decoded history exceeds its size limit.");
        var read = _source.Read(buffer, offset, allowed); _read += read;
        if (_read > _maximum) throw new InvalidDataException("Decoded history exceeds its size limit.");
        return read;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

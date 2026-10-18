using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;

namespace LandingStats.App.GoogleDrive;

internal sealed class BoundedCloudDownload : MemoryStream
{
    private void Check(long end)
    { if (end < 0 || end > CloudApiRequest.MaximumBodyBytes) throw new InvalidDataException("Cloud backup exceeds its download bound; the remote original is retained."); }
    public override void Write(byte[] buffer, int offset, int count) { Check(checked(Position + count)); base.Write(buffer, offset, count); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
    { Check(checked(Position + count)); return base.WriteAsync(buffer, offset, count, token); }
    public override void WriteByte(byte value) { Check(checked(Position + 1)); base.WriteByte(value); }
    public override void SetLength(long value) { Check(value); base.SetLength(value); }
    public override long Position { get => base.Position; set { Check(value); base.Position = value; } }
    public override long Seek(long offset, SeekOrigin loc)
    {
        var end = loc == SeekOrigin.Begin ? offset : checked((loc == SeekOrigin.Current ? Position : Length) + offset); Check(end);
        return base.Seek(offset, loc);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class RuntimeStorageLimits
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public long Bytes { get; set; } = 8L * 1024 * 1024 * 1024;
    [DataMember(Order = 3)] public long MetadataReserve { get; set; } = 64L * 1024 * 1024;
    [DataMember(Order = 4)] public int Quantum { get; set; } = 1024 * 1024;
    public void Validate()
    {
        if (Version != 1 || Quantum < 4096 || Quantum > 1024 * 1024 || MetadataReserve < 4L * Quantum ||
            Bytes < MetadataReserve + 8L * Quantum) throw new InvalidDataException("Invalid runtime storage limits.");
    }
}

/// <summary>One disk budget shared by the application and its workers. A process
/// reserves small blocks under a named mutex, then writes without interprocess
/// I/O on every frame. Reservations deliberately overcount replacements/deletes.
/// A rescan includes two whole blocks for every live writer, covering writes
/// concurrent with the scan. No accepted prefix or pending archive is evicted.</summary>
internal sealed class RuntimeStorageQuota : IDisposable
{
    private const string ConfigName = "storage-limit.json";
    private static readonly object RegistryGate = new object();
    private static readonly Dictionary<string, Lazy<RuntimeStorageQuota>> Registry = new Dictionary<string, Lazy<RuntimeStorageQuota>>(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new object();
    private readonly object _allocationGate = new object();
    private readonly string _root, _control, _counter;
    private readonly RuntimeStorageLimits _limits;
    private readonly Mutex _mutex;
    private readonly FileStream _lease;
    private readonly string _leasePath;
    private long _dataCredit, _metadataCredit, _inflight;
    private bool _disposed;

    private RuntimeStorageQuota(string root, RuntimeStorageLimits? limits)
    {
        _root = root; Directory.CreateDirectory(root);
        _control = Path.Combine(root, ".quota"); Directory.CreateDirectory(_control);
        _counter = Path.Combine(_control, "allocated");
        using var sha = SHA256.Create();
        var name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).Replace("-", "");
        _mutex = new Mutex(false, "Local\\LandingStats.Storage." + name);
        try
        {
            Enter();
            try
            {
                var config = Path.Combine(root, ConfigName);
                _limits = File.Exists(config) ? DurableFile.ReadJson<RuntimeStorageLimits>(config, 4096) : limits ?? new RuntimeStorageLimits();
                _limits.Validate();
                if (!File.Exists(config)) Atomic(config, stream => new DataContractJsonSerializer(typeof(RuntimeStorageLimits)).WriteObject(stream, _limits));
                _leasePath = Path.Combine(_control, Guid.NewGuid().ToString("N") + ".lease");
                // Share reads only: an exclusive probe proves this writer has exited.
                _lease = new FileStream(_leasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                _lease.Write(new byte[8], 0, 8); _lease.Flush(true);
            }
            finally { _mutex.ReleaseMutex(); }
        }
        catch { _mutex.Dispose(); throw; }
    }

    public static RuntimeStorageQuota Attach(string root, RuntimeStorageLimits? limits = null)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        Lazy<RuntimeStorageQuota> value;
        lock (RegistryGate)
        {
            if (!Registry.TryGetValue(root, out value!))
            { value = new Lazy<RuntimeStorageQuota>(() => new RuntimeStorageQuota(root, limits), LazyThreadSafetyMode.ExecutionAndPublication); Registry.Add(root, value); }
        }
        try { return value.Value; }
        catch
        {
            lock (RegistryGate) if (Registry.TryGetValue(root, out var failed) && ReferenceEquals(value, failed)) Registry.Remove(root);
            throw;
        }
    }

    public static Stream Protect(Stream stream, string path, bool leaveOpen = false)
    {
        var full = Path.GetFullPath(path); Lazy<RuntimeStorageQuota>? found = null;
        lock (RegistryGate)
            foreach (var item in Registry)
                if (full.StartsWith(item.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { found = item.Value; break; }
        var quota = found?.Value;
        if (quota == null)
            for (var parent = Path.GetDirectoryName(full); parent != null; parent = Path.GetDirectoryName(parent))
                if (File.Exists(Path.Combine(parent, ConfigName))) { quota = Attach(parent); break; }
        if (quota != null && quota.Exempt(full)) quota = null;
        return new BudgetStream(stream, quota, full.EndsWith(".json", StringComparison.OrdinalIgnoreCase), leaveOpen);
    }

    internal static bool RequiresParticipation(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        lock (RegistryGate)
            if (Registry.Keys.Any(root => full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return true;
        for (var parent = full; parent != null; parent = Path.GetDirectoryName(parent))
            if (File.Exists(Path.Combine(parent, ConfigName))) return true;
        return false;
    }

    private void Enter()
    {
        try { if (!_mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("Runtime storage budget is busy; input remains pending."); }
        catch (AbandonedMutexException) { /* Atomic counter publication is recoverable. */ }
    }
    private void Allocate(bool metadata)
    {
        lock (_allocationGate)
        {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RuntimeStorageQuota));
            if ((metadata ? _metadataCredit : _dataCredit) != 0) return;
        }
        Enter();
        try
        {
            long used;
            try
            {
                using var input = File.OpenRead(_counter); using var reader = new BinaryReader(input);
                if (input.Length != 16) throw new InvalidDataException("Invalid storage counter.");
                var bytes = reader.ReadBytes(16);
                if (BitConverter.ToUInt32(bytes, 0) != 0x3151534c || JournalCrc32.Compute(bytes, 0, 12) != BitConverter.ToUInt32(bytes, 12) ||
                    (used = BitConverter.ToInt64(bytes, 4)) < 0) throw new InvalidDataException("Invalid storage counter.");
            }
            catch (FileNotFoundException) { used = Scan(); }
            catch (InvalidDataException) { used = Scan(); }
            var ceiling = _limits.Bytes - (metadata ? 0 : _limits.MetadataReserve);
            if (used > ceiling - _limits.Quantum) used = Scan();
            if (used > ceiling - _limits.Quantum)
                throw new IOException("Runtime storage quota is full; pending recordings and archives are retained. Delivery or cleanup must free runtime storage before recording can resume.");
            var record = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(0x3151534cU), 0, record, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(checked(used + _limits.Quantum)), 0, record, 4, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(JournalCrc32.Compute(record, 0, 12)), 0, record, 12, 4);
            Atomic(_counter, stream => stream.Write(record, 0, record.Length));
            long reserved;
            lock (_gate)
            {
                if (metadata) _metadataCredit += _limits.Quantum; else _dataCredit += _limits.Quantum;
                reserved = checked(_dataCredit + _metadataCredit + _inflight);
            }
            _lease.Position = 0; var leaseBytes = BitConverter.GetBytes(reserved); _lease.Write(leaseBytes, 0, 8); _lease.Flush();
        }
        finally { _mutex.ReleaseMutex(); }
        }
    }

    private long Scan()
    {
        var live = 0; long reserved = 0;
        foreach (var path in Directory.EnumerateFiles(_control, "*.lease"))
        {
            try { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                live++;
                using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (lease.Length == 8) { using var reader = new BinaryReader(lease); var value = reader.ReadInt64(); if (value < 0) throw new InvalidDataException("Invalid writer reservation."); reserved = checked(reserved + value); }
                else reserved = checked(reserved + 2L * _limits.Quantum); // Earlier runtime writers.
                continue;
            }
            File.Delete(path);
        }
        if (live > 64) throw new IOException("Runtime storage has too many concurrent writers.");
        // Both credit pools may be in flight. Account for all of them even if
        // their writes have already appeared in the physical directory sizes.
        var bytes = reserved;
        var pending = new Stack<string>(); pending.Push(_root); var files = 0;
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    if (++files > 1000000) throw new IOException("Runtime file count exceeds its retention bound.");
                    try { bytes = checked(bytes + new FileInfo(path).Length); }
                    catch (FileNotFoundException) { /* A completed operation removed its scratch file. */ }
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (directory == _root && ExemptDirectory(Path.GetFileName(child))) continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Runtime storage contains a linked directory; quota cannot be established safely.");
                    pending.Push(child);
                }
            }
            catch (DirectoryNotFoundException) { /* Retired scratch was concurrently removed. */ }
        }
        return bytes;
    }

    private void Write(Stream stream, byte[] buffer, int offset, int count, bool metadata)
    {
        var remaining = count; var reserved = 0; Exception? failure = null;
        try
        {
            // Reserve the entire caller write before its first byte. WAL appends
            // submit a complete record, so quota failure cannot tear its header.
            while (remaining > 0)
            {
                var smallMetadata = metadata && stream.Position + reserved < 65536;
                var size = 0;
                lock (_gate)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(RuntimeStorageQuota));
                    var credit = smallMetadata ? _metadataCredit : _dataCredit;
                    size = (int)Math.Min(credit, remaining);
                    if (smallMetadata) size = (int)Math.Min(size, 65536 - stream.Position - reserved);
                    if (smallMetadata) _metadataCredit -= size; else _dataCredit -= size;
                    _inflight += size;
                }
                if (size == 0) { Allocate(smallMetadata); continue; }
                remaining -= size; reserved += size;
            }
            stream.Write(buffer, offset, count);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            lock (_gate) { _inflight -= reserved; Monitor.PulseAll(_gate); }
            // A failed multi-quantum admission must return its reservation even
            // when no subsequent allocation will occur. Small frame writes use
            // the next allocation's update and incur no additional disk I/O.
            if (reserved >= _limits.Quantum || remaining != 0)
                try { RefreshReservation(); }
                catch when (failure != null) { /* The conservative reservation expires with this process; preserve the original write error. */ }
        }
    }
    private void RefreshReservation()
    {
        lock (_allocationGate)
        {
            lock (_gate) if (_disposed) return;
            Enter();
            try
            {
                long reserved; lock (_gate) reserved = checked(_dataCredit + _metadataCredit + _inflight);
                _lease.Position = 0; var bytes = BitConverter.GetBytes(reserved); _lease.Write(bytes, 0, bytes.Length); _lease.Flush();
            }
            finally { _mutex.ReleaseMutex(); }
        }
    }
    private static bool ExemptDirectory(string name) => name.Equals("App", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("engines", StringComparison.OrdinalIgnoreCase) || name.Equals("episode-history", StringComparison.OrdinalIgnoreCase);
    private bool Exempt(string path)
    {
        var relative = path.Substring(_root.Length + 1);
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        return separator >= 0 && ExemptDirectory(relative.Substring(0, separator));
    }
    private static void Atomic(string path, Action<Stream> write)
    {
        var temporary = path + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { write(output); output.Flush(true); }
        DurableFile.PublishTemporary(temporary, path);
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            while (_inflight != 0) Monitor.Wait(_gate);
        }
        lock (_allocationGate) { _lease.Dispose(); _mutex.Dispose(); }
        lock (RegistryGate)
            if (Registry.TryGetValue(_root, out var value) && value.IsValueCreated && ReferenceEquals(value.Value, this)) Registry.Remove(_root);
        try { File.Delete(_leasePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private sealed class BudgetStream : Stream
    {
        private readonly Stream _inner; private readonly RuntimeStorageQuota? _quota; private readonly bool _metadata, _leaveOpen;
        public BudgetStream(Stream inner, RuntimeStorageQuota? quota, bool metadata, bool leaveOpen) { _inner = inner; _quota = quota; _metadata = metadata; _leaveOpen = leaveOpen; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value)
        {
            if (_quota != null && value > _inner.Length) throw new NotSupportedException("Runtime files grow through budgeted writes.");
            _inner.SetLength(value);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (_quota == null) { _inner.Write(buffer, offset, count); return; }
            if (_inner.Position > _inner.Length) throw new NotSupportedException("Sparse runtime writes are not supported.");
            _quota.Write(_inner, buffer, offset, count, _metadata);
        }
        protected override void Dispose(bool disposing) { if (disposing && !_leaveOpen) _inner.Dispose(); base.Dispose(disposing); }
    }
}

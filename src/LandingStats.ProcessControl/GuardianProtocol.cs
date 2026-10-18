using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace LandingStats.App.Runtime;

internal enum GuardianMessageKind : byte { Hello = 1, Status = 2, Stop = 3, Ack = 4, Activate = 5, PrepareRecovery = 6, RecoveryReady = 7, Update = 8, RecoveryDeferred = 9 }
internal enum GuardianStopReason : byte { None, UserExit, SimulatorQuit, SessionEnd, Update }
internal enum GuardianRecovery : byte { None, Presentation, Runtime }
internal sealed class GuardianStatus
{
    internal const int DefaultUiStallSeconds = 30;
    public GuardianStatus(bool ready, long controlQpc, long uiQpc, bool safe, GuardianRecovery recovery = GuardianRecovery.None, string component = "", long shutdownProgressQpc = 0)
    {
        if (controlQpc < 0 || uiQpc < 0 || !Enum.IsDefined(typeof(GuardianRecovery), recovery)) throw new InvalidDataException("Invalid application health.");
        GuardianWire.CheckText(component, 256);
        if (shutdownProgressQpc < 0) throw new InvalidDataException("Invalid shutdown progress.");
        Ready = ready; ControlQpc = controlQpc; UiQpc = uiQpc; SafeToRestart = safe; Recovery = recovery; Component = component; ShutdownProgressQpc = shutdownProgressQpc;
    }
    public bool Ready { get; }
    public long ControlQpc { get; }
    public long UiQpc { get; }
    public bool SafeToRestart { get; }
    public GuardianRecovery Recovery { get; }
    public string Component { get; }
    public long ShutdownProgressQpc { get; }
}
internal sealed class GuardianUpdateRequest
{
    public GuardianUpdateRequest(string executable, string target, string version, string readyEvent, string manifest, long size, string hash, long deadlineQpc = 0)
    {
        GuardianWire.CheckText(executable, 4096); GuardianWire.CheckText(target, 4096); GuardianWire.CheckText(version, 64);
        GuardianWire.CheckText(readyEvent, 128); GuardianWire.CheckText(manifest, 128); GuardianWire.CheckText(hash, 64);
        if (!Path.IsPathRooted(executable) || !Path.IsPathRooted(target) || !System.Version.TryParse(version, out _) || size <= 0 || !GuardianWire.IsToken(hash))
            throw new InvalidDataException("Invalid updater request.");
        Executable = executable; Target = target; Version = version; ReadyEvent = readyEvent; Manifest = manifest; Size = size; Hash = hash;
        DeadlineQpc = deadlineQpc == 0 ? checked(Stopwatch.GetTimestamp() + Stopwatch.Frequency * 12) : deadlineQpc;
    }
    public string Executable { get; }
    public string Target { get; }
    public string Version { get; }
    public string ReadyEvent { get; }
    public string Manifest { get; }
    public long Size { get; }
    public string Hash { get; }
    public long DeadlineQpc { get; }
}
internal sealed class GuardianPacket
{
    public GuardianPacket(GuardianMessageKind kind, long request = 0, string text = "", GuardianStatus? status = null,
        GuardianStopReason stop = GuardianStopReason.None, bool accepted = true, GuardianUpdateRequest? update = null)
    { Kind = kind; Request = request; Text = text; Status = status; Stop = stop; Accepted = accepted; Update = update; }
    public GuardianMessageKind Kind { get; }
    public long Request { get; }
    public string Text { get; }
    public GuardianStatus? Status { get; }
    public GuardianStopReason Stop { get; }
    public bool Accepted { get; }
    public GuardianUpdateRequest? Update { get; }
}
internal sealed class GuardianDescriptor
{
    public const string EnvironmentName = "MSFS_LANDING_STATS_GUARDIAN";
    public GuardianDescriptor(string pipe, Guid session, long generation, string token, int parent)
    {
        if (!pipe.StartsWith("ls-guardian-", StringComparison.Ordinal) || pipe.Length > 128 || !IsPipeName(pipe) || session == Guid.Empty || generation <= 0 ||
            !GuardianWire.IsToken(token) || parent <= 0) throw new InvalidDataException("Invalid guardian bootstrap context.");
        Pipe = pipe; Session = session; Generation = generation; Token = token; Parent = parent;
    }
    public string Pipe { get; }
    public Guid Session { get; }
    public long Generation { get; }
    public string Token { get; }
    public int Parent { get; }
    public static GuardianDescriptor Create(Guid session, long generation)
    {
        var bytes = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return new GuardianDescriptor("ls-guardian-" + Guid.NewGuid().ToString("N"), session, generation,
            BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(), Process.GetCurrentProcess().Id);
    }
    public string Encode() => string.Join(";", "1", Pipe, Session.ToString("N"), Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), Token,
        Parent.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static GuardianDescriptor Parse(string value)
    {
        if (value == null || value.Length > 512) throw new InvalidDataException("Invalid guardian environment.");
        var fields = value.Split(';');
        if (fields.Length != 6 || fields[0] != "1" || !Guid.TryParseExact(fields[2], "N", out var session) ||
            !long.TryParse(fields[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var generation) ||
            !int.TryParse(fields[5], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parent))
            throw new InvalidDataException("Invalid guardian environment.");
        return new GuardianDescriptor(fields[1], session, generation, fields[4], parent);
    }
    private static bool IsPipeName(string value)
    { foreach (var c in value) if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-')) return false; return true; }
}

// Compiled directly into the single-file launcher as well as the App. Bootstrap
// supervision never depends on loading a DLL from an unprepared payload.
internal sealed class GuardianWire : IDisposable
{
    private const int MaximumPacket = 16384;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly Stream _stream;
    private readonly GuardianDescriptor _identity;
    private readonly SemaphoreSlim _writer = new SemaphoreSlim(1, 1);
    private long _sent, _received;
    public GuardianWire(Stream stream, GuardianDescriptor identity) { _stream = stream; _identity = identity; }
    public async Task SendAsync(GuardianPacket packet, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Utf8, true);
            writer.Write(0x3147534c); writer.Write((byte)packet.Kind); writer.Write(_identity.Session.ToByteArray()); writer.Write(_identity.Generation);
            writer.Write(checked(++_sent)); writer.Write(packet.Request);
            switch (packet.Kind)
            {
                case GuardianMessageKind.Hello: Text(writer, packet.Text, 64); break;
                case GuardianMessageKind.Status:
                    var health = packet.Status ?? throw new InvalidDataException("Missing guardian health.");
                    writer.Write(health.Ready); writer.Write(health.ControlQpc); writer.Write(health.UiQpc); writer.Write(health.SafeToRestart);
                    writer.Write((byte)health.Recovery); Text(writer, health.Component, 256); writer.Write(health.ShutdownProgressQpc); break;
                case GuardianMessageKind.Stop:
                    if (packet.Stop == GuardianStopReason.None || !Enum.IsDefined(typeof(GuardianStopReason), packet.Stop)) throw new InvalidDataException("Missing stop intent.");
                    writer.Write((byte)packet.Stop); break;
                case GuardianMessageKind.Ack: writer.Write(packet.Accepted); Text(writer, packet.Text, 256); break;
                case GuardianMessageKind.Update:
                    var update = packet.Update ?? throw new InvalidDataException("Missing update request.");
                    Text(writer, update.Executable, 4096); Text(writer, update.Target, 4096); Text(writer, update.Version, 64);
                    Text(writer, update.ReadyEvent, 128); Text(writer, update.Manifest, 128); writer.Write(update.Size); Text(writer, update.Hash, 64); writer.Write(update.DeadlineQpc); break;
                case GuardianMessageKind.Activate: case GuardianMessageKind.PrepareRecovery: case GuardianMessageKind.RecoveryReady: case GuardianMessageKind.RecoveryDeferred: break;
                default: throw new InvalidDataException("Unknown guardian packet.");
            }
            writer.Flush(); if (memory.Length > MaximumPacket) throw new InvalidDataException("Guardian packet is too large.");
            var length = BitConverter.GetBytes(checked((int)memory.Length));
            await _stream.WriteAsync(length, 0, length.Length, cancellationToken).ConfigureAwait(false);
            var bytes = memory.ToArray(); await _stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }
    public async Task<GuardianPacket> ReadAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4]; await ReadExactly(header, cancellationToken).ConfigureAwait(false); var length = BitConverter.ToInt32(header, 0);
        if (length < 45 || length > MaximumPacket) throw new InvalidDataException("Invalid guardian packet size.");
        var bytes = new byte[length]; await ReadExactly(bytes, cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(bytes, false); using var reader = new BinaryReader(memory, Utf8, true);
        if (reader.ReadInt32() != 0x3147534c) throw new InvalidDataException("Guardian protocol differs.");
        var kind = (GuardianMessageKind)reader.ReadByte();
        if (new Guid(reader.ReadBytes(16)) != _identity.Session || reader.ReadInt64() != _identity.Generation || reader.ReadInt64() != checked(_received + 1))
            throw new InvalidDataException("Guardian generation or sequence differs.");
        var request = reader.ReadInt64(); if (request < 0) throw new InvalidDataException("Invalid guardian request identity.");
        GuardianPacket packet;
        switch (kind)
        {
            case GuardianMessageKind.Hello: packet = new GuardianPacket(kind, request, Text(reader, 64)); break;
            case GuardianMessageKind.Status: packet = new GuardianPacket(kind, request, status: new GuardianStatus(Flag(reader), reader.ReadInt64(),
                reader.ReadInt64(), Flag(reader), (GuardianRecovery)reader.ReadByte(), Text(reader, 256), reader.ReadInt64())); break;
            case GuardianMessageKind.Stop:
                var stop = (GuardianStopReason)reader.ReadByte();
                if (stop == GuardianStopReason.None || !Enum.IsDefined(typeof(GuardianStopReason), stop)) throw new InvalidDataException("Invalid stop intent.");
                packet = new GuardianPacket(kind, request, stop: stop); break;
            case GuardianMessageKind.Ack: var accepted = Flag(reader); packet = new GuardianPacket(kind, request, Text(reader, 256), accepted: accepted); break;
            case GuardianMessageKind.Update:
                packet = new GuardianPacket(kind, request, update: new GuardianUpdateRequest(Text(reader, 4096), Text(reader, 4096), Text(reader, 64),
                    Text(reader, 128), Text(reader, 128), reader.ReadInt64(), Text(reader, 64), reader.ReadInt64())); break;
            case GuardianMessageKind.Activate: case GuardianMessageKind.PrepareRecovery: case GuardianMessageKind.RecoveryReady: case GuardianMessageKind.RecoveryDeferred: packet = new GuardianPacket(kind, request); break;
            default: throw new InvalidDataException("Unknown guardian packet.");
        }
        if (memory.Position != memory.Length) throw new InvalidDataException("Trailing guardian data.");
        _received++; return packet;
    }
    private async Task ReadExactly(byte[] bytes, CancellationToken token)
    {
        var offset = 0;
        while (offset < bytes.Length)
        { var count = await _stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false); if (count == 0) throw new EndOfStreamException(); offset += count; }
    }
    internal static void CheckText(string value, int maximum)
    { if (value == null || value.IndexOf('\0') >= 0 || Utf8.GetByteCount(value) > maximum) throw new InvalidDataException("Invalid guardian text."); }
    private static bool Flag(BinaryReader reader)
    { var value = reader.ReadByte(); if (value > 1) throw new InvalidDataException("Invalid guardian boolean."); return value != 0; }
    private static void Text(BinaryWriter writer, string value, int maximum) { CheckText(value, maximum); var bytes = Utf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
    private static string Text(BinaryReader reader, int maximum)
    {
        var length = reader.ReadInt32(); if (length < 0 || length > maximum) throw new InvalidDataException("Invalid guardian text size.");
        var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); var text = Utf8.GetString(bytes); CheckText(text, maximum); return text;
    }
    internal static bool IsToken(string value)
    { if (value == null || value.Length != 64) return false; foreach (var c in value) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false; return true; }
    public void Dispose() { _stream.Dispose(); } // Do not dispose the semaphore under an already accepted writer.
}
internal static class GuardianPipes
{
    public static NamedPipeServerStream Create(string name)
    {
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        using (var current = WindowsIdentity.GetCurrent()) security.AddAccessRule(new PipeAccessRule(current.User ?? throw new InvalidOperationException("Missing user identity."),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, security);
    }
    public static void VerifyClient(NamedPipeServerStream pipe, int expected)
    { if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id) || id != expected) throw new InvalidDataException("Guardian peer is not its owned process."); }
    public static void VerifyServer(NamedPipeClientStream pipe, int expected)
    { if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var id) || id != expected) throw new InvalidDataException("Guardian server identity differs."); }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);
}

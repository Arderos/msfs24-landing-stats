using System;
using System.IO;
using LandingStats.Core;

namespace LandingStats.Runtime.Contracts;

// Re-establish the observed SDK state after a transport loss, including events
// inside the lost range. This is not a new SDK observation or a flight boundary.
public sealed class SimulatorLifecycleSnapshotMessage : RuntimePayload
{
    private const int Replay = 32, Ended = 64;
    public SimulatorLifecycleSnapshotMessage(bool connected = false, bool quit = false, bool known = false,
        int running = -1, int pause = -1, int blocks = 0, int cancelled = 0, SdkReplayState replay = SdkReplayState.Unknown)
    {
        if (running < -1 || running > 1 || blocks < 0 || blocks > 127 || cancelled < 0 || cancelled > 127 ||
            !Enum.IsDefined(typeof(SdkReplayState), replay) || connected && quit)
            throw new ArgumentException("Invalid simulator lifecycle snapshot.");
        Connected = connected; Quit = quit; Known = known; Running = running; Pause = pause;
        Blocks = blocks; Cancelled = cancelled; SdkReplay = replay;
    }
    public bool Connected { get; }
    public bool Quit { get; }
    public bool Known { get; }
    public int Running { get; }
    public int Pause { get; }
    public int Blocks { get; }
    public int Cancelled { get; }
    public SdkReplayState SdkReplay { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.SimulatorLifecycleSnapshot;

    public SimulatorLifecycleSnapshotMessage Observe(LifecycleMessage message)
    {
        if (message.State == SourceLifecycle.Connected) return new SimulatorLifecycleSnapshotMessage(connected: true);
        if (message.State == SourceLifecycle.Disconnected || message.State == SourceLifecycle.SimulatorQuit)
            return new SimulatorLifecycleSnapshotMessage(quit: Quit || message.State == SourceLifecycle.SimulatorQuit);
        var running = Running; var pause = Pause; var blocks = Blocks; var cancelled = Cancelled; var replay = SdkReplay; var known = Known;
        switch (message.State)
        {
            case SourceLifecycle.PauseFlags: pause = message.Value; known = true; break;
            case SourceLifecycle.SimRunning: running = message.Value != 0 ? 1 : 0; known = true; break;
            case SourceLifecycle.Flow:
                known = true;
                // The versioned adapter uses paired start/end values 1..12 for
                // loading, teleport, back-on-track, skip, RTC and replay.
                var flow = message.Value;
                if (flow >= 1 && flow <= 12)
                {
                    var block = 1 << ((flow - 1) / 2);
                    if ((flow & 1) != 0)
                    {
                        if ((blocks & block) == 0)
                        { cancelled &= ~block; if (block == 1) blocks &= ~Ended; blocks |= block; }
                    }
                    else if ((blocks & block) == 0 && (cancelled & block) != 0) cancelled &= ~block;
                    else { blocks &= ~block; running = -1; }
                    if (block == Replay) replay = (flow & 1) != 0 ? SdkReplayState.Active : SdkReplayState.Inactive;
                }
                else if (flow >= 13 && flow <= 15) // main menu, flight end, crash
                {
                    if ((blocks & Ended) == 0) { cancelled |= blocks & ~Ended; blocks = Ended; }
                    replay = SdkReplayState.Unknown;
                }
                else if (flow == 16 && (blocks & Ended) != 0) { blocks &= ~Ended; running = -1; }
                break;
        }
        return new SimulatorLifecycleSnapshotMessage(Connected, Quit, known, running, pause, blocks, cancelled, replay);
    }
    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Connected); writer.Write(Quit); writer.Write(Known); writer.Write(Running); writer.Write(Pause);
        writer.Write(Blocks); writer.Write(Cancelled); writer.Write((sbyte)SdkReplay);
    }
    internal static SimulatorLifecycleSnapshotMessage Read(BinaryReader reader) => new SimulatorLifecycleSnapshotMessage(
        RuntimeWire.ReadBoolean(reader), RuntimeWire.ReadBoolean(reader), RuntimeWire.ReadBoolean(reader),
        reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), (SdkReplayState)reader.ReadSByte());
}

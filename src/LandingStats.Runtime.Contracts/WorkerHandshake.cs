using System;
using System.IO;

namespace LandingStats.Runtime.Contracts;

public sealed class WorkerHandshake
{
    private readonly WorkerIdentity _worker;
    private readonly string _build;
    private readonly string _token;
    private readonly ulong _requiredCapabilities;
    private readonly RuntimeClockEpoch _clock;
    private bool _accepted;

    public WorkerHandshake(WorkerIdentity worker, string build, string token, ulong requiredCapabilities, RuntimeClockEpoch clock)
    {
        _worker = worker; _build = build; _token = token; _requiredCapabilities = requiredCapabilities; _clock = clock;
        _ = new WorkerHello(build, token, requiredCapabilities, clock.Id, clock.Frequency);
    }

    public void Accept(RuntimeEnvelope message)
    {
        if (_accepted || !_worker.Equals(message.Worker) || message.Sequence != 1 || message.ConnectionGeneration != 0 ||
            !(message.Payload is WorkerHello hello) || hello.Build != _build || !SameToken(hello.LaunchToken, _token) ||
            hello.ClockEpochId != _clock.Id || hello.QpcFrequency != _clock.Frequency || message.CaptureQpc < _clock.OriginQpc)
            throw new InvalidDataException("Worker handshake rejected.");
        if ((hello.Capabilities & _requiredCapabilities) != _requiredCapabilities)
            throw new InvalidDataException((_requiredCapabilities & RuntimeCapabilities.SharedStorageQuota) != 0 && (hello.Capabilities & RuntimeCapabilities.SharedStorageQuota) == 0
                ? "Retained worker does not support the shared storage quota; original inputs remain pending."
                : "Worker lacks required runtime capabilities; original inputs remain pending.");
        _accepted = true;
    }

    private static bool SameToken(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var difference = 0;
        for (var i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
        return difference == 0;
    }
}

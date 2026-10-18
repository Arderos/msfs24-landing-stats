using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace LandingStats.App.Runtime;

internal sealed class GuardianGeneration : IDisposable
{
    private readonly GuardianDescriptor _descriptor;
    private readonly NamedPipeServerStream _pipe;
    private readonly GuardianWire _wire;
    private readonly GuardianSignals _signals;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly Func<GuardianUpdateRequest, CancellationToken, Task<GuardianUpdateLease>>? _update;
    private readonly object _transitionGate = new object();
    private OwnedProcess? _process;
    private GuardianStatus? _health;
    private int _stop, _recoveryReady, _recoveryDeferred;
    private Task _completion = Task.CompletedTask;
    private long _lastRequest;
    private int _disposed;
    public GuardianGeneration(Guid session, long generation, Func<GuardianUpdateRequest, CancellationToken, Task<GuardianUpdateLease>>? update = null)
    {
        _descriptor = GuardianDescriptor.Create(session, generation); _update = update;
        _signals = GuardianSignals.Create(_descriptor);
        try { _pipe = GuardianPipes.Create(_descriptor.Pipe); _wire = new GuardianWire(_pipe, _descriptor); }
        catch { _signals.Dispose(); throw; }
    }
    public int ProcessId => _process?.Id ?? 0;
    public Task<int> Exit => _process?.Exit ?? Task.FromResult(0);
    public Task Completion => _completion;
    public GuardianStatus? Health => Volatile.Read(ref _health);
    public GuardianStopReason StopIntent
    {
        get
        {
            lock (_transitionGate)
            {
            var requested = _signals.ObserveStop();
            if (requested != GuardianStopReason.None) Interlocked.CompareExchange(ref _stop, (int)requested, (int)GuardianStopReason.None);
            var reason = (GuardianStopReason)Volatile.Read(ref _stop);
            if (reason != GuardianStopReason.None) _signals.AcknowledgeStop();
            return reason;
            }
        }
    }
    public bool RecoveryReady => Volatile.Read(ref _recoveryReady) != 0;
    public bool TakeRecoveryDeferred() => Interlocked.Exchange(ref _recoveryDeferred, 0) != 0;
    public async Task StartAsync(string executable, IReadOnlyList<string> arguments, string directory, IReadOnlyDictionary<string, string>? environment,
        TimeSpan deadline, bool visible = true)
    {
        if (_process != null || Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("Guardian generation was already started or retired.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (environment != null) foreach (var pair in environment) values.Add(pair.Key, pair.Value);
        values[GuardianDescriptor.EnvironmentName] = _descriptor.Encode();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(deadline);
        using var abort = timeout.Token.Register(() => _pipe.Dispose());
        lock (_transitionGate)
        {
            timeout.Token.ThrowIfCancellationRequested();
            _process = OwnedProcess.Start(executable, arguments, directory, jobName: "Local\\MSFS.LandingStats.App." + _descriptor.Session.ToString("N") + "." + _descriptor.Generation,
                environment: values, hideWindow: !visible);
        }
        var connection = _pipe.WaitForConnectionAsync(timeout.Token);
        if (await Task.WhenAny(connection, _process.Exit).ConfigureAwait(false) != connection)
            throw new IOException("Application exited during guardian connection: " + await _process.Exit.ConfigureAwait(false));
        await connection.ConfigureAwait(false); GuardianPipes.VerifyClient(_pipe, _process.Id);
        var handshake = _wire.ReadAsync(timeout.Token);
        if (await Task.WhenAny(handshake, _process.Exit).ConfigureAwait(false) != handshake)
            throw new IOException("Application exited during guardian handshake: " + await _process.Exit.ConfigureAwait(false));
        var hello = await handshake.ConfigureAwait(false);
        if (hello.Kind != GuardianMessageKind.Hello || hello.Request != 1 || !string.Equals(hello.Text, _descriptor.Token, StringComparison.Ordinal))
            throw new InvalidDataException("Application guardian handshake differs.");
        await _wire.SendAsync(new GuardianPacket(GuardianMessageKind.Ack, hello.Request), timeout.Token).ConfigureAwait(false);
        _lastRequest = 1; _completion = ReadPumpAsync();
    }
    private async Task ReadPumpAsync()
    {
        while (true)
        {
            var packet = await _wire.ReadAsync(_lifetime.Token).ConfigureAwait(false);
            if (packet.Kind == GuardianMessageKind.Status)
            { if (packet.Request != 0) throw new InvalidDataException("Health cannot own a control request."); Volatile.Write(ref _health, packet.Status); continue; }
            if (packet.Kind == GuardianMessageKind.RecoveryReady)
            { if (packet.Request != 0) throw new InvalidDataException("Invalid recovery acknowledgement."); Interlocked.Exchange(ref _recoveryReady, 1); continue; }
            if (packet.Kind == GuardianMessageKind.RecoveryDeferred)
            { if (packet.Request != 0) throw new InvalidDataException("Invalid recovery deferral."); Interlocked.Exchange(ref _recoveryDeferred, 1); continue; }
            if (packet.Request <= _lastRequest) throw new InvalidDataException("Guardian request identity was reused.");
            _lastRequest = packet.Request;
            if (packet.Kind == GuardianMessageKind.Stop)
            {
                // Update intent is accepted only after a verified helper has
                // confirmed it can replace this exact resident launcher.
                if (packet.Stop == GuardianStopReason.Update && StopIntent != GuardianStopReason.Update)
                    throw new InvalidDataException("An update has not been prepared.");
                Interlocked.CompareExchange(ref _stop, (int)packet.Stop, (int)GuardianStopReason.None);
                _signals.AcknowledgeStop();
                await _wire.SendAsync(new GuardianPacket(GuardianMessageKind.Ack, packet.Request), _lifetime.Token).ConfigureAwait(false);
            }
            else if (packet.Kind == GuardianMessageKind.Update)
            {
                var accepted = false;
                if (StopIntent == GuardianStopReason.None && _update != null)
                {
                    GuardianUpdateLease? prepared = null;
                    try
                    {
                        var request = packet.Update!;
                        var remaining = (request.DeadlineQpc - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
                        if (remaining <= 0 || remaining > 15) throw new TimeoutException("Updater request deadline expired or differs.");
                        using var handoff = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        handoff.CancelAfter(TimeSpan.FromSeconds(remaining));
                        prepared = await _update(request, handoff.Token).ConfigureAwait(false);
                        lock (_transitionGate)
                        {
                            _lifetime.Token.ThrowIfCancellationRequested();
                            handoff.Token.ThrowIfCancellationRequested();
                            if (Stopwatch.GetTimestamp() >= request.DeadlineQpc) throw new TimeoutException("Updater request expired before commit.");
                            if (StopIntent == GuardianStopReason.None)
                            {
                                Interlocked.Exchange(ref _stop, (int)GuardianStopReason.Update);
                                try { prepared.Commit(); }
                                catch { Interlocked.CompareExchange(ref _stop, (int)GuardianStopReason.None, (int)GuardianStopReason.Update); throw; }
                                _signals.AcceptUpdate(); accepted = true;
                            }
                        }
                    }
                    catch (Exception) when (!_lifetime.IsCancellationRequested) { /* Rejected handoff leaves the application running. */ }
                    finally { if (prepared != null) await prepared.RetireAsync().ConfigureAwait(false); }
                }
                await _wire.SendAsync(new GuardianPacket(GuardianMessageKind.Ack, packet.Request, accepted: accepted), _lifetime.Token).ConfigureAwait(false);
            }
            else throw new InvalidDataException("Unexpected application control packet.");
        }
    }
    public Task ActivateAsync()
    { AllowSetForegroundWindow(ProcessId); return _wire.SendAsync(new GuardianPacket(GuardianMessageKind.Activate), _lifetime.Token); }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
    public void Cancel() { lock (_transitionGate) { try { _lifetime.Cancel(); _pipe.Dispose(); } catch (ObjectDisposedException) { } } }
    public Task PrepareRecoveryAsync() => _wire.SendAsync(new GuardianPacket(GuardianMessageKind.PrepareRecovery), _lifetime.Token);
    public async Task RetireAsync(TimeSpan deadline)
    {
        lock (_transitionGate) _lifetime.Cancel();
        // Even after the root process exited, helpers belong to this job until
        // the entire tree is confirmed empty. Only then may a successor start.
        if (_process != null) await _process.TerminateAsync(deadline).ConfigureAwait(false);
        _lifetime.Cancel(); _wire.Dispose();
        try { await _completion.ConfigureAwait(false); } catch { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _wire.Dispose(); _process?.Dispose(); _signals.Dispose();
        _ = _completion.ContinueWith(task => { _ = task.Exception; _lifetime.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

internal sealed class GuardianSupervisor
{
    private readonly GuardianPolicy _policy;
    private readonly Func<long, GuardianGeneration> _factory;
    private readonly object _lifecycle = new object();
    private GuardianGeneration? _current;
    private int _stop;
    public GuardianSupervisor(Func<long, GuardianGeneration> factory, GuardianPolicy? policy = null)
    { _factory = factory; _policy = policy ?? new GuardianPolicy(); _policy.Validate(); }
    public GuardianGeneration? Current => Volatile.Read(ref _current);
    public event Action<long, string>? Changed;
    public void Stop() { lock (_lifecycle) { Interlocked.Exchange(ref _stop, 1); Current?.Cancel(); } }
    public async Task RunAsync(string executable, IReadOnlyList<string> arguments, string directory, IReadOnlyDictionary<string, string>? environment = null, bool visible = true)
    {
        var failures = 0; long generation = 0; var startupFailures = 0;
        while (Volatile.Read(ref _stop) == 0)
        {
            GuardianGeneration prepared;
            try { prepared = _factory(++generation); }
            catch (Exception error)
            {
                Publish(generation, "prepare_failed:" + error.GetType().Name);
                failures = Math.Min(1000000, failures + 1);
                if (++startupFailures >= _policy.MaximumStartupFailures) throw new IOException("The application repeatedly failed during runtime preparation.", error);
                await Task.Delay(_policy.Backoff(failures)).ConfigureAwait(false); continue;
            }
            using var child = prepared;
            lock (_lifecycle)
            {
                if (Volatile.Read(ref _stop) != 0) return;
                Volatile.Write(ref _current, child);
            }
            var started = Stopwatch.GetTimestamp(); long preparing = 0; var intentional = false;
            try
            {
                Publish(generation, "starting");
                await child.StartAsync(executable, arguments, directory, environment, _policy.Startup, visible).ConfigureAwait(false);
                string? previous = null; var lastTick = Stopwatch.GetTimestamp(); long resumeUntil = 0;
                while (Volatile.Read(ref _stop) == 0)
                {
                    var now = Stopwatch.GetTimestamp();
                    // A suspended machine/process makes every old QPC sample
                    // stale at once. Allow a bounded new health observation.
                    if (now - lastTick > Stopwatch.Frequency * 5) { resumeUntil = now + (long)(_policy.ResumeGrace.TotalSeconds * Stopwatch.Frequency); preparing = 0; }
                    lastTick = now;
                    if (child.TakeRecoveryDeferred()) preparing = 0;
                    var decision = _policy.Decide(now, started, child.Health, child.StopIntent, child.Exit.IsCompleted, child.Completion.IsCompleted, preparing, child.RecoveryReady);
                    if (now < resumeUntil && child.StopIntent == GuardianStopReason.None && !child.Exit.IsCompleted && !child.Completion.IsCompleted)
                    { decision = GuardianDecision.Continue; if (child.Health?.ControlQpc >= resumeUntil - (long)(_policy.ResumeGrace.TotalSeconds * Stopwatch.Frequency) && child.Health?.UiQpc >= resumeUntil - (long)(_policy.ResumeGrace.TotalSeconds * Stopwatch.Frequency)) resumeUntil = 0; }
                    var state = decision.ToString(); if (state != previous) { Publish(generation, state); previous = state; }
                    if (decision == GuardianDecision.Stop)
                    {
                        intentional = true;
                        var stopping = Stopwatch.GetTimestamp(); var progressAt = stopping; var lastProgress = child.Health?.ShutdownProgressQpc ?? 0;
                        while (!child.Exit.IsCompleted && Stopwatch.GetTimestamp() - stopping < _policy.MaximumShutdown.TotalSeconds * Stopwatch.Frequency)
                        {
                            var progress = child.Health?.ShutdownProgressQpc ?? 0;
                            if (progress > lastProgress && progress <= Stopwatch.GetTimestamp()) { lastProgress = progress; progressAt = Stopwatch.GetTimestamp(); }
                            if (Stopwatch.GetTimestamp() - progressAt >= _policy.ShutdownIdle.TotalSeconds * Stopwatch.Frequency) break;
                            await Task.WhenAny(child.Exit, Task.Delay(_policy.CheckInterval)).ConfigureAwait(false);
                        }
                        break;
                    }
                    if (decision == GuardianDecision.Restart) break;
                    if (decision == GuardianDecision.PrepareRecovery)
                    { preparing = now; await child.PrepareRecoveryAsync().ConfigureAwait(false); }
                    if (child.Health?.Ready == true && now - started > Stopwatch.Frequency * 60 && decision == GuardianDecision.Continue) failures = 0;
                    await Task.Delay(_policy.CheckInterval).ConfigureAwait(false);
                }
            }
            catch (Exception error) { Publish(generation, "failed:" + error.GetType().Name); }
            finally
            {
                // Never abandon an unconfirmed tree and continue the outer
                // loop. Repeated termination failures retain this owner.
                while (true)
                {
                    try { await child.RetireAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); break; }
                    catch (Exception error) when (error is IOException || error is TimeoutException || error is System.ComponentModel.Win32Exception)
                    { Publish(generation, "retirement_pending:" + error.GetType().Name); await Task.Delay(250).ConfigureAwait(false); }
                }
                Volatile.Write(ref _current, null);
            }
            if (intentional || child.StopIntent != GuardianStopReason.None || Volatile.Read(ref _stop) != 0) return;
            if (child.Health?.Ready != true)
            { if (++startupFailures >= _policy.MaximumStartupFailures) throw new IOException("The application repeatedly failed before becoming ready. See crash.log and guardian.log."); }
            else startupFailures = 0;
            failures = Math.Min(1000000, failures + 1); await Task.Delay(_policy.Backoff(failures)).ConfigureAwait(false);
        }
    }
    private void Publish(long generation, string value) { try { Changed?.Invoke(generation, value); } catch { } }
}

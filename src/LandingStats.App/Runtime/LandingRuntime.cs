using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Storage;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.App.TelemetryUpload;
using LandingStats.App.GoogleDrive;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

/// <summary>Application services own durable state independently of the window.
/// Native acquisition, analysis and HTTP execute in replaceable process jobs;
/// single managed owners commit their results and project bounded UI state.</summary>
internal sealed class LandingRuntime : IDisposable
{
    private readonly IDisposable _ownership, _sessionOwnership;
    private readonly RuntimeStorageQuota _storageQuota;
    private readonly ApplicationRuntime _acquisition;
    private readonly EpisodeRuntimeOwner _episodes;
    private readonly RuntimeRawExportOwner _raw;
    private readonly UploadRuntimeOwner _uploads;
    private readonly RuntimePresentation _presentation;
    private readonly ArchiveIntakeOwner _intake;
    private readonly IsolatedGoogleDriveApi? _cloudApi;
    private readonly CloudSyncRuntimeOwner? _cloud;
    private readonly IntegrationApiRuntimeOwner _integrationApi;
    private readonly IntegrationTelemetrySnapshot _integrationTelemetry = new IntegrationTelemetrySnapshot();
    private readonly List<ComponentSupervisor> _owners = new List<ComponentSupervisor>();
    private readonly SemaphoreSlim _policyGate = new SemaphoreSlim(1, 1);
    private readonly OperationLifetime _operations = new OperationLifetime();
    private readonly object _lifecycle = new object();
    private Task? _stop;
    private bool _started, _stopping;
    private int _ready;
    private long _policyRequest;
    private bool _automaticRequested;
    private IReadOnlyList<AirportFacility> _knownAirports = Array.Empty<AirportFacility>();

    public LandingRuntime(string root, string deployment, string uploadIdentityRoot, LandingRepository repository, object repositoryGate,
        Func<RuntimePresentationSnapshot, CancellationToken, Task> present, Action<string, long> escalate,
        Func<ComponentRole, long, string?>? fixture = null, ComponentRecoveryOptions? recoveryOptions = null,
        int rawChunkSize = RawCaptureRepository.ChunkMaximumSamples, string? legacyQueue = null,
        AirportFacilityRepository? airportRepository = null, object? airportGate = null, RuntimeCloudServices? cloud = null)
    {
        Root = Path.GetFullPath(root); _ownership = RuntimeSession.AcquireOwnership(Root);
        IDisposable? sessionOwnership = null; RuntimeStorageQuota? storageQuota = null;
        try
        {
            _storageQuota = storageQuota = RuntimeStorageQuota.Attach(Root);
            var engines = Path.Combine(Root, "engines"); var scratch = Path.Combine(Root, "jobs"); var sessions = Path.Combine(Root, "sessions");
            Engine = ProcessingEngineLease.Capture(engines, deployment);
            Identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
            SessionRoot = Path.Combine(sessions, Identity.RuntimeId.ToString("N"));
            _sessionOwnership = sessionOwnership = RuntimeSession.AcquireOwnership(SessionRoot);
            RuntimeSession.Create(SessionRoot, Identity, Engine.Identity);
            Archives = Path.Combine(Root, "archives");
            Store = new EpisodeTransactionStore(Path.Combine(Root, "episodes"), Engine.Identity);
            Outbox = new DurableOutbox(Path.Combine(Root, "outbox"), Archives);
            var archivePolicy = new CaptureArchivePolicy(Path.Combine(Root, "receiver-capabilities.json"));
            _presentation = new RuntimePresentation(present);
            Outbox.RecoveryFailed += (id, error) => _presentation.Failure("delivery.outbox", id, error);
            _ = new TelemetryUploadIdentityStore(uploadIdentityRoot).Identity();
            airportRepository ??= new AirportFacilityRepository(Path.Combine(Root, "airports.gz")); airportGate ??= new object();
            lock (airportGate) _knownAirports = airportRepository.Load();
            var worker = Path.Combine(Engine.DirectoryPath, "LandingStats.WorkerHost.exe");
            _integrationApi = new IntegrationApiRuntimeOwner(() => new IntegrationApiData(Path.Combine(Root, "integration"),
                repository, repositoryGate, Store, () => Snapshot, () => Health, _integrationTelemetry.Read),
                worker, Identity.RuntimeId, Identity.Clock, escalate, _presentation.Health,
                error => _presentation.Failure("integration.api", Guid.Empty, error),
                recoveryOptions ?? RuntimeComponentCatalog.Create().Single(item => item.Component == RuntimeComponent.IntegrationApi).Options,
                generation => fixture?.Invoke(ComponentRole.IntegrationApi, generation));
            if (cloud != null)
            {
                var cloudRoot = Path.Combine(Root, "cloud");
                _cloudApi = new IsolatedGoogleDriveApi(cloudRoot, worker, cloud.Tokens, Identity.RuntimeId, Identity.Clock, generation => fixture?.Invoke(ComponentRole.CloudSync, generation));
                _cloud = new CloudSyncRuntimeOwner(Path.Combine(cloudRoot, "sync.json"), _cloudApi, cloud.Backup, _presentation.Cloud, cloud.Completed);
                cloud.Binding.Bind(_cloudApi);
            }
            var catalogWorker = new RetainedAnalysisExecutor(engines, scratch, Store, Identity.RuntimeId, Identity.Clock, generation => fixture?.Invoke(ComponentRole.Analysis, generation));
            var geometryCatalog = new GeometryCatalogRuntimeOwner(catalogWorker.RefreshGeometryCatalogAsync,
                error => _presentation.Failure("analysis.catalog", Guid.Empty, error.ToString()));
            var analysis = new RetainedAnalysisExecutor(engines, scratch, Store, Identity.RuntimeId, Identity.Clock, generation => fixture?.Invoke(ComponentRole.Analysis, generation), geometryCatalog.WaitReadyAsync);
            var recovery = new RetainedAnalysisExecutor(engines, scratch, Store, Identity.RuntimeId, Identity.Clock, generation => fixture?.Invoke(ComponentRole.Analysis, generation));
            _uploads = new UploadRuntimeOwner(Outbox, worker, uploadIdentityRoot, Identity.RuntimeId, Identity.Clock, _presentation.Upload,
                generation => fixture?.Invoke(ComponentRole.Upload, generation), archivePolicy.Observe);
            _intake = new ArchiveIntakeOwner(Path.Combine(Root, "intake"), Archives, legacyQueue, uploadIdentityRoot, Store, Outbox, _uploads.Wake, archivePolicy.UseZstd);
            _episodes = new EpisodeRuntimeOwner(Store,
                new EpisodeWorkCoordinator(Store, null, repository, repositoryGate, analysis, _integrationApi.Publish),
                new BugReportRepository(Archives, archivePolicy.UseZstd), Outbox, update =>
                {
                    if (update.Processed.AirportFacilities.Count != 0)
                    {
                        IReadOnlyList<AirportFacility> airports;
                        lock (airportGate) airports = airportRepository.MergeAndSave(update.Processed.AirportFacilities);
                        HistoryCommands.LoadSummaries(repository, repositoryGate, airports, CancellationToken.None);
                        Volatile.Write(ref _knownAirports, airports);
                    }
                    _presentation.Episode(update);
                    if (update.Report != null) _cloud?.RequestBackground();
                });
            _episodes.Failed += (id, error) => _presentation.Failure("analysis.episodes", id, error);
            var raw = new RawEffectStore(Path.Combine(SessionRoot, "raw"));
            var rawBatches = new RawBatchPersistence(raw);
            _raw = new RuntimeRawExportOwner(sessions, SessionRoot, raw, new RawChunkStore(Path.Combine(Root, "chunks"), Archives, Outbox, archivePolicy.UseZstd, automatic: true), _uploads.Wake, rawChunkSize, rawBatches.Flush);
            var previous = new RuntimeSessionRecoveryOwner(sessions, SessionRoot, recovery, () => { _episodes.Wake(); _raw.Wake(); });
            var effects = new RecordingEffectPersistence(Store, _episodes.Wake, (input, values) =>
            { rawBatches.Commit(input, values); _raw.Wake(); return Task.CompletedTask; }, () => Volatile.Read(ref _knownAirports));
            _acquisition = new ApplicationRuntime(SessionRoot, Identity, worker, async (input, values) =>
            { await effects.CommitAsync(input, values).ConfigureAwait(false); _presentation.Recording(Identity, values);
                _integrationTelemetry.Observe(values, input.Input.TimestampUtc); geometryCatalog.Observe(values); }, escalate, fixture, recoveryOptions, rawBatches.FlushAtCheckpointAsync);
            _acquisition.HealthChanged += _presentation.Health;
            var catalog = RuntimeComponentCatalog.Create();
            void Register(string name, RuntimeComponent component, Func<ManagedComponentContext, Task> run)
            {
                var supervisor = new ComponentSupervisor(name, generation => new ManagedComponentGeneration(generation, run), _ => Task.CompletedTask, escalate,
                    recoveryOptions ?? catalog.Single(item => item.Component == component).Options);
                supervisor.Changed += _presentation.Health; _owners.Add(supervisor);
            }
            Register("analysis.episodes", RuntimeComponent.Analysis, _episodes.RunAsync);
            Register("analysis.catalog", RuntimeComponent.Analysis, geometryCatalog.RunAsync);
            Register("recovery.sessions", RuntimeComponent.Analysis, previous.RunAsync);
            Register("storage.raw", RuntimeComponent.HistoryArchiveOutbox, _raw.RunAsync);
            Register("storage.reports", RuntimeComponent.HistoryArchiveOutbox, _intake.RunAsync);
            Register("storage.retention", RuntimeComponent.HistoryArchiveOutbox, new RuntimeRetentionOwner(Root, SessionRoot, Store, (id, error) => _presentation.Failure("storage.retention", id, error)).RunAsync);
            Register("delivery.telemetry", RuntimeComponent.TelemetryUpload, _uploads.RunAsync);
            Register("presentation", RuntimeComponent.Presentation, _presentation.RunAsync);
            Register("integration.data", RuntimeComponent.IntegrationApi, _integrationApi.RunAsync);
            if (_cloud != null) Register("cloud.sync", RuntimeComponent.CloudSync, _cloud.RunAsync);
        }
        catch { sessionOwnership?.Dispose(); storageQuota?.Dispose(); _ownership.Dispose(); throw; }
    }
    public string Root { get; }
    public string SessionRoot { get; }
    public string Archives { get; }
    public RecoveryJournalIdentity Identity { get; }
    public ProcessingEngineLease Engine { get; }
    internal EpisodeTransactionStore Store { get; }
    internal DurableOutbox Outbox { get; }
    internal ApplicationRuntime Acquisition => _acquisition;
    public RuntimePresentationSnapshot Snapshot => _presentation.Snapshot;
    public bool CloudEnabled => _cloudApi?.Enabled == true;
    public bool IntegrationApiEnabled => _integrationApi.Enabled;
    internal IntegrationApiRuntimeOwner IntegrationApi => _integrationApi;
    public bool Ready => Volatile.Read(ref _ready) != 0;
    public bool SafeToRestart => !Volatile.Read(ref _stopping) && Ready && _acquisition.SafeToRestart;
    // The caller first awaits StartAsync; a stopped runtime cannot grant restart.
    public async Task WaitForSafeRestartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycle)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Ready || _stopping) throw new InvalidOperationException("Runtime is not available for a safe restart.");
        }
        await _acquisition.WaitForSafeRestartAsync(cancellationToken).ConfigureAwait(false);
        lock (_lifecycle)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping) throw new InvalidOperationException("Runtime has stopped accepting a restart.");
        }
    }
    // Presentation may be unable to retire while the dispatcher is stalled.
    // Its escalation is deferred to a recording-safe boundary by the guardian.
    internal bool IsPresentationDependency(string component, bool uiStalled) => component == "presentation" || uiStalled && component == "cloud.sync" && _cloud?.WaitingForPresentation == true;
    public long ObservationQpc => ReadObservationQpc(false);
    internal long ReadObservationQpc(bool uiStalled) => Math.Min(_acquisition.ObservationQpc,
        Math.Min(_integrationApi.ObservationQpc, _owners.Where(owner => !IsPresentationDependency(owner.Snapshot.Component, uiStalled)).Min(owner => owner.ObservationQpc)));
    internal bool NeedsRecovery(string component, long generation) => !Health.Any(health => health.Component == component && health.Generation > generation);
    public IReadOnlyList<AirportFacility> KnownAirports => Volatile.Read(ref _knownAirports);
    public IReadOnlyList<SupervisionSnapshot> Health => _acquisition.Health.Concat(_owners.Select(owner => owner.Snapshot)).Concat(new[] { _integrationApi.Snapshot }).ToArray();

    public async Task SetIntegrationApiEnabledAsync(bool enabled)
    {
        using var accepted = AcceptReport();
        await _integrationApi.SetEnabledAsync(enabled).ConfigureAwait(false);
    }

    public async Task StartAsync(bool automaticAllowed, bool rawCapture = false, CancellationToken cancellationToken = default)
    {
        IDisposable startup; Task policyGate;
        lock (_lifecycle)
        {
            if (_started || _stopping) throw new InvalidOperationException("Landing runtime cannot be started twice.");
            _started = true; startup = _operations.TryEnter()!;
            _automaticRequested = automaticAllowed; ++_policyRequest;
            _ = _uploads.SetAutomaticAllowed(false);
            policyGate = _policyGate.WaitAsync(cancellationToken);
        }
        using (startup)
        {
            // Return to the caller after registering intent, before disk or
            // owner startup. UI publication may hold a short synchronous gate.
            await Task.Yield();
            await policyGate.ConfigureAwait(false);
            try
            {
                lock (_lifecycle)
                {
                    if (_stopping) return;
                    foreach (var owner in _owners) owner.Start();
                    _acquisition.StartOwners();
                }
                // A preference may change during any durable startup command.
                // Reconcile until stable before any source can produce frames
                // or the upload owner can deliver automatic archives.
                while (true)
                {
                    long request; bool automatic;
                    lock (_lifecycle)
                    {
                        if (_stopping) return;
                        request = _policyRequest; automatic = _automaticRequested;
                    }
                    var policy = await Task.Run(() => Outbox.SetAutomaticEnabled(automatic), cancellationToken).ConfigureAwait(false);
                    await _acquisition.ApplyDeliveryPolicyAsync(policy, cancellationToken).ConfigureAwait(false);
                    await _acquisition.SetRawCaptureAsync(rawCapture, cancellationToken).ConfigureAwait(false);
                    lock (_lifecycle)
                    {
                        if (_stopping) return;
                        if (request != _policyRequest) continue;
                        _ = _uploads.SetAutomaticAllowed(automatic);
                        _acquisition.StartSources();
                        Volatile.Write(ref _ready, 1);
                        break;
                    }
                }
            }
            finally { _policyGate.Release(); }
        }
    }
    public async Task SetAutomaticDeliveryAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        IDisposable operation; long request;
        Task uploadStopped = Task.CompletedTask;
        lock (_lifecycle)
        {
            if (!_started || _stopping) throw new InvalidOperationException("Landing runtime is not accepting policy changes.");
            operation = _operations.TryEnter()!; request = ++_policyRequest;
            _automaticRequested = enabled;
            if (!enabled) uploadStopped = _uploads.SetAutomaticAllowed(false);
        }
        using (operation)
        {
            try
            {
                await _policyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    lock (_lifecycle) if (request != _policyRequest) return;
                    var policy = await Task.Run(() => Outbox.SetAutomaticEnabled(enabled), cancellationToken).ConfigureAwait(false);
                    await _acquisition.ApplyDeliveryPolicyAsync(policy, cancellationToken).ConfigureAwait(false);
                    lock (_lifecycle) if (request == _policyRequest) _ = _uploads.SetAutomaticAllowed(enabled);
                    _episodes.Wake();
                }
                finally { _policyGate.Release(); }
            }
            finally { await uploadStopped.ConfigureAwait(false); }
        }
    }
    public Task SetRawCaptureAsync(bool enabled, CancellationToken cancellationToken = default) => _acquisition.SetRawCaptureAsync(enabled, cancellationToken);
    public void WakeDelivery() => _uploads.Wake();
    public Task<GoogleDriveSyncResult> RequestCloudSyncAsync()
    {
        var accepted = AcceptReport();
        return Task.Run(() =>
        {
            using (accepted) return (_cloud ?? throw new GoogleDriveNotSignedInException()).Request();
        });
    }
    public async Task SetCloudEnabledAsync(bool enabled)
    {
        using var accepted = AcceptReport();
        if (_cloudApi == null) throw new GoogleDriveNotSignedInException();
        await _cloudApi.SetEnabledAsync(enabled).ConfigureAwait(false); _cloud!.Wake();
    }
    public async Task<string?> SignInCloudAsync(CancellationToken cancellationToken)
    {
        using var accepted = AcceptReport();
        if (_cloudApi == null) throw new GoogleDriveNotSignedInException();
        return await _cloudApi.SignInAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task SignOutCloudAsync(CancellationToken cancellationToken)
    {
        using var accepted = AcceptReport();
        if (_cloudApi == null) throw new GoogleDriveNotSignedInException();
        await _cloudApi.SignOutAsync(cancellationToken).ConfigureAwait(false); _cloud!.Wake();
    }
    public Guid RequestManualReport(Guid episode)
    {
        using var accepted = AcceptReport(); return _intake.RequestManual(episode);
    }
    public Task<Guid> RequestManualReportAsync(Guid episode)
    {
        var accepted = AcceptReport();
        return Task.Run(() => { using (accepted) return _intake.RequestManual(episode); });
    }
    private IDisposable AcceptReport()
    {
        lock (_lifecycle)
        {
            if (!_started || _stopping) throw new InvalidOperationException("Landing runtime is not accepting reports.");
            return _operations.TryEnter()!;
        }
    }
    public Task StopAsync()
    {
        lock (_lifecycle)
        {
            if (_stop != null) return _stop;
            _stopping = true; return _stop = Task.Run(StopOwnedAsync);
        }
    }
    private async Task StopOwnedAsync()
    {
        var failures = new List<Exception>();
        async Task Attempt(Func<Task> step)
        { try { await step().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); } }
        void Release(Action step)
        { try { step(); } catch (Exception error) { failures.Add(error); } }
        try
        {
            _cloud?.StopIntake();
            var cloudStop = _cloudApi?.StopAsync() ?? Task.CompletedTask;
            var integrationStop = _integrationApi.StopAsync();
            await Attempt(() => _operations.Stop());
            await Attempt(() => _acquisition.StopAsync());
            await Attempt(() => Task.WhenAll(_owners.Select(owner => owner.StopAsync())));
            await Attempt(() => cloudStop);
            await Attempt(() => integrationStop);
        }
        finally
        {
            Release(_acquisition.Dispose); foreach (var owner in _owners) Release(owner.Dispose);
            Release(_sessionOwnership.Dispose); Release(_storageQuota.Dispose); Release(_ownership.Dispose);
        }
        if (failures.Count != 0) throw new AggregateException("Runtime stopped with pending recovery work.", failures);
    }
    public void Dispose() { _ = StopAsync(); }
}

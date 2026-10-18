using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

/// <summary>A retained processing engine reads immutable inputs and writes one
/// private result. Only its parent is allowed to commit history or manifests.</summary>
internal sealed class AnalysisWorkerRole : IWorkerRole
{
    private readonly Func<FlightModelGeometryResolver>? _geometry;
    public AnalysisWorkerRole(Func<FlightModelGeometryResolver>? geometry = null) { _geometry = geometry; }
    public async Task RunAsync(WorkerExecutionContext context)
    {
        var owner = new WorkerOwner(context); AnalysisJobMessage? job = null;
        Task<AnalysisResultMessage>? pending = null; long started = 0;
        CommandResultMessage Command(RuntimeEnvelope envelope)
        {
            if (!(envelope.Payload is AnalysisJobMessage request)) return new CommandResultMessage(CommandOutcome.Rejected, "unsupported_analysis_command");
            if (job != null)
            {
                if (job.Job != request.Job) return new CommandResultMessage(CommandOutcome.Rejected, "analysis_generation_already_assigned");
                if (job.Episode != request.Episode || job.Engine != request.Engine || job.InputRoot != request.InputRoot || job.OutputDirectory != request.OutputDirectory || job.Operation != request.Operation || job.Simulator != request.Simulator)
                    return new CommandResultMessage(CommandOutcome.Rejected, "analysis_job_id_reused");
                return new CommandResultMessage(CommandOutcome.Completed, "analysis_job_exists");
            }
            job = request; started = Stopwatch.GetTimestamp(); pending = Task.Run(() => AnalyzeAsync(request));
            return new CommandResultMessage(CommandOutcome.Completed, "analysis_job_accepted");
        }
        while (true)
        {
            var completed = pending?.IsCompleted == true;
            if (completed)
            {
                owner.Publish(await pending!.ConfigureAwait(false), Stopwatch.GetTimestamp()); pending = null;
            }
            owner.Progress(completed, pending == null ? DependencyWait.Idle : DependencyWait.Storage, Command,
                pending == null ? 0 : started, pending == null ? Guid.Empty : job!.Job);
            if (owner.Stopping) return;
            await Task.Delay(50, context.CancellationToken).ConfigureAwait(false);
        }
    }
    private async Task<AnalysisResultMessage> AnalyzeAsync(AnalysisJobMessage request)
    {
        try
        {
            using var scratch = RuntimeScratchLease.EnterWorker(request.OutputDirectory);
            var binary = Path.GetDirectoryName(typeof(AnalysisWorkerRole).Assembly.Location)!;
            var engine = ProcessingEngineLease.Open(Path.GetDirectoryName(binary)!, request.Engine);
            if (!string.Equals(engine.DirectoryPath, binary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Analysis worker is not running its requested retained engine.");
            if (!Path.IsPathRooted(request.InputRoot) || !Path.IsPathRooted(request.OutputDirectory)) throw new InvalidDataException("Analysis paths must be absolute.");
            var catalogDirectory = Path.Combine(Path.GetDirectoryName(request.InputRoot)!, "geometry-index");
            if (request.Operation == AnalysisOperation.RefreshGeometryCatalog)
            {
                var resolver = _geometry?.Invoke() ?? new FlightModelGeometryResolver(request.Simulator, catalogDirectory);
                var preparedPath = Path.Combine(request.OutputDirectory, "result.json");
                await resolver.WritePreparedCatalogAsync(preparedPath).ConfigureAwait(false);
                return new AnalysisResultMessage(request.Job, EpisodeTransactionStore.Hash(preparedPath), new FileInfo(preparedPath).Length);
            }
            if (request.Operation == AnalysisOperation.RecoverRecording)
            {
                ColdRecordingRecovery.Recover(request.InputRoot, request.OutputDirectory, request.Engine, request.Episode);
                var recovered = Path.Combine(request.OutputDirectory, "result.json");
                return new AnalysisResultMessage(request.Job, EpisodeTransactionStore.Hash(recovered), new FileInfo(recovered).Length);
            }
            var store = new EpisodeTransactionStore(request.InputRoot, request.Engine); var manifest = store.Read(request.Episode);
            store.RequireEngine(manifest); var input = store.ReadInputs(manifest);
            if (request.Operation == AnalysisOperation.ResolveGeometry || request.Operation == AnalysisOperation.ResolvePreparedGeometry)
            {
                var geometryResolver = input.Episode.IsDiagnosticOnly ? new FlightModelGeometryResolver(Array.Empty<string>(), false) :
                    _geometry?.Invoke() ?? (request.Operation == AnalysisOperation.ResolvePreparedGeometry
                        ? FlightModelGeometryResolver.FromPreparedCatalog(FlightModelGeometryResolver.PreparedCatalogPath(catalogDirectory, input.Episode.Simulator))
                        : new FlightModelGeometryResolver(input.Episode.Simulator, catalogDirectory));
                var preparer = new EpisodeProcessor(geometryResolver, new LandingRepository(Path.Combine(request.OutputDirectory, "unused-history")), new object());
                var frozen = await preparer.ResolveGeometryAsync(input, manifest).ConfigureAwait(false);
                var geometryPath = Path.Combine(request.OutputDirectory, "result.json"); DurableFile.WriteJson(geometryPath, frozen);
                return new AnalysisResultMessage(request.Job, EpisodeTransactionStore.Hash(geometryPath), new FileInfo(geometryPath).Length);
            }
            ProcessedEpisode result;
            if (request.FinalizeReport) result = EpisodeWorkCoordinator.BuildReport(input, store.ReadAnalysis(manifest), store.ReadFacilities(manifest));
            else
            {
                var processor = new EpisodeProcessor(new FlightModelGeometryResolver(Array.Empty<string>(), false),
                    new LandingRepository(Path.Combine(request.OutputDirectory, "unused-history")), new object());
                result = await processor.ProcessAsync(input.Episode, input.Metadata.KnownAirports, input.Metadata.TimestampUtc, persistHistory: false,
                    geometryInputs: store.ReadGeometry(manifest)).ConfigureAwait(false);
            }
            var path = Path.Combine(request.OutputDirectory, "result.json");
            DurableFile.WriteJson(path, new EpisodeAnalysisData
            {
                Id = manifest.Id, Engine = manifest.Engine, InputHash = manifest.InputHash, ReplayLike = result.ReplayLike,
                FacilityHash = request.FinalizeReport ? manifest.FacilityHash : "",
                GeometryHash = manifest.GeometryHash,
                Airports = result.AirportFacilities.ToArray(), Records = result.Records.Select(LandingRecordFile.FromRecord).ToArray(),
                Decisions = DecisionPersistenceData.Capture(result.Decisions)
            });
            return new AnalysisResultMessage(request.Job, EpisodeTransactionStore.Hash(path), new FileInfo(path).Length);
        }
        catch (Exception error)
        { return new AnalysisResultMessage(request.Job, "", 0, OptionalMetadataText.Sanitize(error.GetType().Name + ": " + error.Message, 256, "analysis_failed")); }
    }
}

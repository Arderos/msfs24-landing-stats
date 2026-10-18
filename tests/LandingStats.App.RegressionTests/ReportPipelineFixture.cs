using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

/// <summary>Private repositories around the production episode pipeline. No UI,
/// simulator, network or substitute implementation of report generation.</summary>
internal sealed class ReportPipelineFixture : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "report-pipeline-" + Guid.NewGuid().ToString("N"));
    public readonly LandingRepository Repository;
    public readonly object RepositoryGate = new object();
    public readonly EpisodeTransactionStore Store;
    public readonly EpisodeProcessor Processor;
    public readonly BugReportRepository Archives;
    public readonly DurableOutbox Outbox;
    public readonly EpisodeRuntimeOwner Owner;
    public readonly Dictionary<Guid, BugReportCandidate> Reports = new Dictionary<Guid, BugReportCandidate>();

    public ReportPipelineFixture()
    {
        Repository = new LandingRepository(Path.Combine(Root, "history"));
        Store = new EpisodeTransactionStore(Path.Combine(Root, "episodes"), new string('a', 64));
        Processor = new EpisodeProcessor(new FlightModelGeometryResolver(Array.Empty<string>(), false), Repository, RepositoryGate);
        Archives = new BugReportRepository(Path.Combine(Root, "archives"));
        Outbox = new DurableOutbox(Path.Combine(Root, "outbox"), Archives.RootPath);
        Outbox.SetAutomaticEnabled(true);
        Owner = CreateOwner(Processor);
    }

    public EpisodeRuntimeOwner CreateOwner(EpisodeProcessor processor) => new EpisodeRuntimeOwner(Store,
        new EpisodeWorkCoordinator(Store, processor, Repository, RepositoryGate), Archives, Outbox,
        update => { if (update.Report != null) Reports[update.Episode] = update.Report; });

    public EpisodeWorkManifest Accept(LandingEpisodeEventArgs episode, ProcessedEpisode? analysis = null, DateTime? timestamp = null)
    {
        var manifest = Store.CommitInputs(episode, timestamp ?? DateTime.UtcNow, Array.Empty<AirportFacility>(),
            Outbox.Policy.AutomaticEnabled, Outbox.Policy.Revision);
        if (analysis != null) Store.CommitAnalysis(manifest, analysis);
        return manifest;
    }

    public void Finalize(LandingEpisodeEventArgs episode, EpisodeFacilitySnapshot? facilities = null) =>
        Store.CommitFacilities(new RuntimeFinalFacilities(episode.PersistentId, facilities ?? episode.InitialFacilities, episode.DecisionJournal?.Snapshot()));

    public bool Advance(Guid id) => Complete(Owner.AdvanceAsync(id));
    public string Archive(Guid id) => Store.Read(id).ArchivePath;
    public static T Complete<T>(Task<T> task)
    {
        if (!task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Production report pipeline fixture timed out.");
        return task.GetAwaiter().GetResult();
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

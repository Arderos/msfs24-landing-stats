using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LandingStats.App;
using LandingStats.App.Controls;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.TelemetryUpload;
using LandingStats.App.Updates;
using LandingStats.Core;
using UpdaterProgram = LandingStats.App.Updater.Program;

namespace LandingStats.App.RegressionTests;

internal static class Program
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        _ = new System.Windows.Application();
        LocalizationManager.Apply("en");

        Run("deduplicator keeps the last same-time frame", DeduplicatorKeepsLastFrame);
        Run("approach timeout re-arms capture", ApproachTimeoutRearmsCapture);
        Run("paused simulator frames do not grow the active episode", PausedFramesDoNotGrowEpisode);
        Run("pre-roll uses monotonic receipt time and remains bounded", PreRollUsesReceiptTimeAndRemainsBounded);
        Run("full telemetry gate has AGL hysteresis and RAW override", FullTelemetryGateHasHysteresis);
        Run("raw debug streams into a temporary queue zip", RawDebugStreamsIntoZip);
        Run("full telemetry toggle is absent from the main window", FullTelemetryToggleIsAbsent);
        Run("landing episode ids pair retention start and completion", LandingEpisodeIdsPairStartAndCompletion);
        Run("bug report retention expires on the next landing sequence", BugReportRetentionExpiresOnNextSequence);
        Run("bug report archive contains telemetry and calculated results", BugReportArchiveContainsTelemetryAndResults);
        Run("bug report is persisted before network preparation", BugReportPersistsBeforeNetworkPreparation);
        Run("window shutdown drains local bug report persistence", WindowShutdownDrainsBugReportPersistence);
        Run("raw capture startup removes only abandoned temp chunks", RawCaptureCleansAbandonedTempChunks);
        Run("raw telemetry queue never blocks its producer", RawTelemetryQueueNeverBlocksProducer);
        Run("telemetry identity is stable, protected, and signs", TelemetryIdentityIsStableAndSigns);
        Run("telemetry registration is automatic and hardware anonymous", TelemetryRegistrationIsAutomaticAndAnonymous);
        Run("existing telemetry waits for enrollment and preserves prior consent", ExistingTelemetryWaitsForEnrollmentAndConsent);
        Run("legacy RAW startup retry requires persisted consent", LegacyRawStartupRetryRequiresPersistedConsent);
        Run("telemetry byte limit rejects before scheduling", TelemetryByteLimitRejectsBeforeScheduling);
        Run("durable telemetry backlog drains after queue saturation", TelemetryBacklogDrainsAfterSaturation);
        Run("pending bug report recovers after enrollment failure", PendingBugReportRecoversAfterEnrollmentFailure);
        Run("oversized backlog entry does not starve a valid report", OversizedBacklogEntryDoesNotStarveValidReport);
        Run("upload re-enrolls after server invalidates enrollment", UploadReenrollsAfterForbiddenResponse);
        Run("upload worker survives a read-only queue file", UploadWorkerSurvivesReadOnlyQueueFile);
        Run("corrupt telemetry identity does not fault the upload worker", CorruptTelemetryIdentityDoesNotFaultWorker);
        Run("telemetry enqueue is safe during disposal", TelemetryEnqueueIsSafeDuringDisposal);
        Run("permanently rejected telemetry is quarantined once", PermanentlyRejectedTelemetryIsQuarantined);
        Run("updater accepts a signed manifest and rejects tampering", UpdaterVerifiesSignedManifest);
        Run("updater accepts the issued format-2 manifest shape", UpdaterAcceptsIssuedManifestShape);
        Run("updater accepts the format-3 single executable manifest shape", UpdaterAcceptsSingleExecutableManifestShape);
        Run("updater preserves bridge and current manifest channels", UpdaterPreservesManifestChannels);
        Run("updater accepts browser-renamed executable targets", UpdaterAcceptsBrowserRenamedTarget);
        Run("updater extracts only one bundled executable from legacy package", UpdaterExtractsLegacySingleExecutable);
        Run("updater cleanup refuses paths outside its private root", UpdaterCleanupRefusesUnsafePath);
        Run("updater installs one valid executable transactionally", UpdaterInstallsSingleExecutable);
        Run("updater rolls back an invalid executable replacement", UpdaterRollsBackInvalidExecutableReplacement);
        Run("valid frame clears transient error state", ValidFrameClearsTransientErrorState);
        Run("MSFS replay flow events disable and re-arm landing capture", ReplayFlowEventsDisableCapture);
        Run("recorder reports airborne state for replay overlay", RecorderReportsAirborneState);
        Run("replay frames do not alter live state or raw telemetry", ReplayFramesDoNotAlterLiveStateOrRawTelemetry);
        Run("replay kinematic inconsistencies are rejected conservatively", ReplayKinematicInconsistenciesAreRejected);
        Run("legacy SimConnect controller path is absent", LegacyControllerPathIsAbsent);
        Run("compact frame matches the SimConnect payload contract", CompactFrameMatchesPayloadContract);
        Run("telemetry schema v5 reads current and v4 rows", TelemetrySchemaReadsCurrentAndV4Rows);
        Run("telemetry CSV preserves doubles and rejects header-row schema mismatch", TelemetryCsvIsStrictAndRoundTrips);
        Run("telemetry receiver header exactly matches the client schema", TelemetryReceiverHeaderMatchesClient);
        Run("header wind uses the contact-time sample", HeaderWindUsesContactTimeSample);
        Run("landing history uses lazy columnar v7 details", LandingHistoryUsesLazyColumnarDetails);
        Run("landing delete removes detail and index entry", LandingDeleteRemovesDetailAndIndexEntry);
        Run("landing index reconciles committed details after an interrupted save", LandingIndexReconcilesCommittedDetails);
        Run("landing filenames are culture invariant", LandingFilenamesAreCultureInvariant);
        Run("airport cache is never overwritten after a transient read failure", AirportCacheSurvivesTransientReadFailure);
        Run("corrupt airport cache is quarantined and rebuilt", CorruptAirportCacheIsRebuilt);
        Run("episode airport snapshots cannot roll back a newer refresh", EpisodeAirportSnapshotKeepsNewerRefresh);
        Run("chart ranges discard non-finite telemetry", ChartRangesDiscardNonFiniteTelemetry);
        Run("chart value ticks always label a visible zero", ChartValueTicksIncludeZero);
        Run("lane hover finds the nearest point without sorting", LaneHoverFindsNearestPoint);
        Run("A340 gear chart groups wheels into four struts", A340GearChartGroupsFourStruts);
        Run("A340 gear chart survives crosswind timing and airborne window end", A340GearChartSurvivesCrosswindAndTouchAndGo);
        Run("A340 gear chart excludes helpers when the nose never touches", A340GearChartExcludesHelpersWithoutNoseContact);
        Run("fourth-engine throttle uses the fourth-engine color", FourthEngineThrottleUsesMatchingColor);
        Run("closure reconstruction survives history round-trip", ClosureReconstructionSurvivesHistoryRoundTrip);
        Run("legacy reconstruction provenance remains telemetry", LegacyReconstructionProvenanceRemainsTelemetry);
        Run("bounce history shows the latest contact first", BounceHistoryShowsLatestContactFirst);
        Run("monthly average counts each landing once", MonthlyAverageCountsPrimaryContactsOnly);
        Run("columnar v7 is smaller than the object layout", ColumnarV7IsSmallerThanObjectLayout);
        Run("stored controller columns retain only live sources", StoredControllerColumnsAreCompact);
        Run("closure reconstruction keeps the raw latch independent", ClosureReconstructionKeepsRawLatchIndependent);
        Run("closure reconstruction requires five fit points", ClosureReconstructionRequiresFiveFitPoints);
        Run("closure reconstruction requires five distinct timestamps", ClosureReconstructionRequiresDistinctTimestamps);
        Run("closure reconstruction never extrapolates before its history", ClosureReconstructionRequiresEvaluationBracket);
        Run("closure reconstruction sanitizes an early ground spike", ClosureReconstructionSanitizesEarlyGroundSpike);
        Run("closure reconstruction is unavailable without geometry", ClosureReconstructionRequiresGeometry);
        Run("closure reconstruction marks last-air contact-time fallback", ClosureReconstructionMarksContactTimeFallback);
        Run("closure reconstruction accepts permuted staggered mains", ClosureReconstructionAcceptsPermutedStaggeredMains);
        Run("closure reconstruction accepts A340 center main conservatively", ClosureReconstructionAcceptsCenterMainConservatively);
        Run("closure reconstruction accepts clustered A340 wheel contacts", ClosureReconstructionAcceptsClusteredA340Wheels);
        Run("closure reconstruction accepts irregular A340 wheel timing", ClosureReconstructionAcceptsIrregularA340WheelTiming);
        Run("closure reconstruction accepts four mains and one nose point", ClosureReconstructionAcceptsFivePointTopology);
        Run("configured gear topology ignores unrelated contact helpers", ConfiguredGearTopologyIgnoresHelpers);
        Run("closure reconstruction keeps sustained main-gear bounces", ClosureReconstructionKeepsMainGearBounces);
        Run("closure reconstruction rejects mixed main-nose contact", ClosureReconstructionRejectsMixedMainNoseContact);
        Run("closure reconstruction rejects nose-first contact without changing raw metrics", ClosureReconstructionRejectsNoseFirstContact);
        Run("telemetry geometry recovers an analytic arm from permuted points", TelemetryGeometryRecoversAnalyticArm);
        Run("datum calibration rejects inconsistent phases", DatumCalibrationRejectsInconsistentPhases);
        Run("flight model parser merges modular geometry and named points", FlightModelParserMergesModularGeometry);
        Run("flight model resolver maps aircraft title and model", FlightModelResolverMapsAircraftIdentity);
        Run("flight model async failures fall back to telemetry", FlightModelAsyncFailuresFallBackToTelemetry);
        Run("language auto-detection is Russian-only with an English fallback", LanguageAutoDetectionIsRussianOnly);
        Run("English landing dates use an English month and 24-hour time", EnglishLandingDateFormatIsStable);
        Run("settings preserve unknown future options", SettingsPreserveUnknownFutureOptions);
        Run("legacy settings leave simulator auto-start undecided", LegacySettingsLeaveAutoStartUndecided);
        Run("Google Drive onboarding follows simulator auto-start", GoogleDriveOnboardingFollowsAutoStart);
        Run("settings repository reports missing and corrupt files", SettingsRepositoryReportsUnreadableFiles);
        Run("settings recovery marker cannot crash startup when locked", SettingsRecoveryMarkerIsBestEffort);
        Run("Google OAuth uses PKCE and the per-file Drive scope", GoogleDriveBackupRegressionTests.OAuthUsesPkceAndDriveFileScope);
        Run("Google OAuth refresh tokens are protected for the Windows user", GoogleDriveBackupRegressionTests.TokenStoreProtectsRefreshToken);
        Run("Google Drive backup synchronizes and only propagates in-app deletes", GoogleDriveBackupRegressionTests.BackupSynchronizesAndPropagatesDeletes);
        Run("Google Drive account switches start with a non-destructive union", GoogleDriveBackupRegressionTests.AccountSwitchStartsWithSafeUnion);
        Run("Google Drive deletion survives an in-flight account switch", GoogleDriveBackupRegressionTests.DeleteDuringAccountSwitchReturnsToOriginalAccount);
        Run("Google Drive delete intent is durable during an active sync", GoogleDriveBackupRegressionTests.DeleteWaitsForActiveSyncAndIsNotLost);
        Run("Google Drive v3 state migration preserves a late delete", GoogleDriveBackupRegressionTests.LegacyStateMigrationPreservesLateDelete);
        Run("Google Drive offline delete intent survives restart", GoogleDriveBackupRegressionTests.OfflineDeleteSurvivesRestart);
        Run("Google Drive late delete cannot be resurrected by an active download", GoogleDriveBackupRegressionTests.LateDeleteCannotBeResurrected);
        Run("Google Drive in-app deletion propagates across devices", GoogleDriveBackupRegressionTests.InAppDeletePropagatesAcrossDevices);
        Run("Google Drive in-app deletion claims an existing manual trash entry", GoogleDriveBackupRegressionTests.InAppDeleteClaimsManuallyTrashedLanding);
        Run("Google Drive pending deletion survives an account round trip", GoogleDriveBackupRegressionTests.PendingDeleteSurvivesAccountRoundTrip);
        Run("Google Drive unscoped deletion never applies to a first account", GoogleDriveBackupRegressionTests.UnscopedDeleteNeverAppliesToFirstAccount);
        Run("local landing deletion does not require Google Drive state storage", GoogleDriveBackupRegressionTests.LocalDeleteDoesNotRequireDriveStateStorage);
        Run("Google Drive identical first-sync duplicates converge", GoogleDriveBackupRegressionTests.IdenticalFirstSyncDuplicatesConverge);
        Run("Google Drive simultaneous first sign-in keeps one logical backup", GoogleDriveBackupRegressionTests.SimultaneousFirstSignInConvergesToOneRoot);
        Run("Google Drive canonical root changes preserve delete intent", GoogleDriveBackupRegressionTests.CanonicalRootChangePreservesDeleteIntent);
        Run("Google Drive trashed canonical roots preserve delete intent", GoogleDriveBackupRegressionTests.TrashedCanonicalRootPreservesDeleteIntent);
        Run("Google Drive landing conflicts do not block unrelated synchronization", GoogleDriveBackupRegressionTests.ConcurrentLandingEditsAreRejectedWithoutLoss);
        Run("Google Drive sibling revisions are isolated from unrelated synchronization", GoogleDriveBackupRegressionTests.SiblingRevisionConflictsAreIsolated);
        Run("Google Drive merges independent landing metadata resolution", GoogleDriveBackupRegressionTests.MetadataOnlyLandingConflictMergesResolvedValues);
        Run("Google Drive restores unreadable local settings from cloud", GoogleDriveBackupRegressionTests.MissingLocalSettingsRestoreFromCloud);
        Run("Google Drive settings recovery survives an offline restart", GoogleDriveBackupRegressionTests.SettingsRecoverySurvivesOfflineRestart);
        Run("Google Drive concurrent settings edits converge", GoogleDriveBackupRegressionTests.ConcurrentSettingsEditsAreRejectedWithoutLoss);
        Run("Google Drive settings changed during download are preserved", GoogleDriveBackupRegressionTests.SettingsChangedDuringDownloadArePreserved);
        Run("Google Drive future settings remain untouched", GoogleDriveBackupRegressionTests.FutureSettingsRemainUntouched);
        Run("Google Drive unchanged landings use a lightweight fingerprint", GoogleDriveBackupRegressionTests.UnchangedLandingSyncUsesLightweightFingerprint);
        Run("application lifetime rejects a second instance", ApplicationLifetimeRejectsSecondInstance);
        Run("simulator auto-start preserves every foreign byte", SimulatorAutoStartPreservesForeignBytes);
        Run("simulator auto-start creates a valid missing exe.xml", SimulatorAutoStartCreatesMissingConfiguration);
        Run("simulator auto-start supports Windows-1252 and Unicode paths", SimulatorAutoStartSupportsLegacyEncoding);
        Run("simulator auto-start preserves an UTF-8 BOM", SimulatorAutoStartPreservesUtf8Bom);
        Run("simulator auto-start refuses malformed XML without partial writes", SimulatorAutoStartRefusesMalformedConfiguration);
        Run("simulator auto-start leaves unmanaged matching entries alone", SimulatorAutoStartLeavesUnmanagedEntryAlone);
        Run("simulator auto-start prepares all profiles before committing", SimulatorAutoStartIsTransactionalAcrossProfiles);
        Run("simulator auto-start discovers Store and Steam profiles", SimulatorAutoStartDiscoversKnownProfiles);
        Run("simulator auto-start never enables MSFS 2020 and removes the v0.8.2 entry", SimulatorAutoStartCleansUnsupported2020Entry);
        Run("simulator auto-start retains a legacy cleanup error", SimulatorAutoStartRetainsLegacyCleanupError);
        Run("WPF resources switch completely between English and Russian", WpfResourcesSwitchCompletely);
        Run("localized whitespace survives WPF resource loading", LocalizedWhitespaceSurvivesWpfLoading);
        Run("landing feel severity is independent of translated text", LandingFeelSeverityIsLanguageIndependent);

        Console.WriteLine(_failures == 0
            ? "All regression tests passed."
            : $"{_failures} regression test(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void LanguageAutoDetectionIsRussianOnly()
    {
        Equal("ru", LocalizationManager.ResolveLanguage("auto", CultureInfo.GetCultureInfo("ru-RU")), "Russian automatic language");
        Equal("en", LocalizationManager.ResolveLanguage("auto", CultureInfo.GetCultureInfo("en-US")), "English automatic language");
        Equal("en", LocalizationManager.ResolveLanguage("auto", CultureInfo.GetCultureInfo("de-DE")), "unsupported locale fallback");
        Equal("ru", LocalizationManager.ResolveLanguage("ru", CultureInfo.GetCultureInfo("de-DE")), "explicit Russian override");
        Equal("en", LocalizationManager.ResolveLanguage("en", CultureInfo.GetCultureInfo("ru-RU")), "explicit English override");
    }

    private static void EnglishLandingDateFormatIsStable()
    {
        var value = new DateTime(2026, 8, 5, 21, 37, 0, DateTimeKind.Local);
        Equal(
            "05 Aug · 21:37",
            value.ToString("dd MMM · HH:mm", LocalizationManager.CultureFor("en")),
            "English landing timestamp");
    }

    private static void SettingsPreserveUnknownFutureOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-settings-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            Directory.CreateDirectory(root);
            var repository = new ApplicationSettingsRepository(path);
            repository.Save(new ApplicationSettings { Language = "en" });
            var issued = File.ReadAllText(path, Encoding.UTF8).TrimEnd();
            File.WriteAllText(
                path,
                issued.Substring(0, issued.Length - 1) + ",\"futureOption\":{\"enabled\":true}}",
                new UTF8Encoding(false));

            var settings = repository.Load();
            Equal("en", settings.Language, "stored language");

            repository.Save(settings);
            var saved = File.ReadAllText(path, Encoding.UTF8);
            Equal(true, saved.Contains("\"futureOption\""), "unknown option survives save");

            settings.Language = "de";
            repository.Save(settings);
            Equal("auto", repository.Load().Language, "unsupported saved preference normalizes to automatic");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void LegacySettingsLeaveAutoStartUndecided()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-legacy-settings-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"language\":\"en\"}", new UTF8Encoding(false));
            var repository = new ApplicationSettingsRepository(path);
            var settings = repository.Load();
            Null(settings.StartWithSimulator, "legacy setting remains undecided");
            Equal(false, settings.GoogleDrivePromptAnswered, "legacy Google Drive prompt remains unanswered");
            Equal(ApplicationSettings.CurrentSchemaVersion, settings.SchemaVersion, "legacy schema upgrades in memory");

            settings.StartWithSimulator = false;
            settings.GoogleDrivePromptAnswered = true;
            repository.Save(settings);
            Equal(false, repository.Load().StartWithSimulator, "explicit refusal persists");
            Equal(true, repository.Load().GoogleDrivePromptAnswered, "Google Drive prompt answer persists");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void GoogleDriveOnboardingFollowsAutoStart()
    {
        var freshInstall = new ApplicationSettings();
        Equal(
            false,
            MainWindow.ShouldShowGoogleDrivePrompt(freshInstall, signedIn: false, autoStartPromptVisible: true),
            "fresh install waits for the simulator auto-start question");
        Equal(
            true,
            MainWindow.ShouldShowGoogleDrivePrompt(freshInstall, signedIn: false, autoStartPromptVisible: false),
            "fresh install asks about Google Drive after simulator auto-start");

        var updatedInstall = new ApplicationSettings { StartWithSimulator = false };
        Equal(
            true,
            MainWindow.ShouldShowGoogleDrivePrompt(updatedInstall, signedIn: false, autoStartPromptVisible: false),
            "updated install asks only the new Google Drive question");

        updatedInstall.GoogleDrivePromptAnswered = true;
        Equal(
            false,
            MainWindow.ShouldShowGoogleDrivePrompt(updatedInstall, signedIn: false, autoStartPromptVisible: false),
            "answered Google Drive question does not return");

        freshInstall.GoogleDrivePromptAnswered = false;
        Equal(
            false,
            MainWindow.ShouldShowGoogleDrivePrompt(freshInstall, signedIn: true, autoStartPromptVisible: false),
            "an existing Google Drive sign-in suppresses onboarding");

        Equal(
            true,
            MainWindow.ShouldRestoreSettingsBeforeOnboarding(
                signedIn: true,
                settingsPersistedAndReadable: false),
            "signed-in startup restores missing settings before either onboarding prompt");
        Equal(
            false,
            MainWindow.ShouldRestoreSettingsBeforeOnboarding(
                signedIn: false,
                settingsPersistedAndReadable: false),
            "signed-out fresh install does not wait for unavailable cloud settings");
        Equal(
            false,
            MainWindow.ShouldRestoreSettingsBeforeOnboarding(
                signedIn: true,
                settingsPersistedAndReadable: true),
            "readable local settings do not delay onboarding");
    }

    private static void SettingsRepositoryReportsUnreadableFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-unreadable-settings-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            Directory.CreateDirectory(root);
            var repository = new ApplicationSettingsRepository(path);
            Equal(false, repository.TryLoad(out var missing), "missing settings read status");
            Equal("auto", missing.Language, "missing settings fallback");

            File.WriteAllText(path, "{not-json", new UTF8Encoding(false));
            Equal(false, repository.TryLoad(out var corrupt), "corrupt settings read status");
            Equal("auto", corrupt.Language, "corrupt settings fallback");

            repository.Save(new ApplicationSettings { Language = "ru" });
            Equal(true, repository.TryLoad(out var valid), "valid settings read status");
            Equal("ru", valid.Language, "valid settings value");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void SettingsRecoveryMarkerIsBestEffort()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "landing-stats-marker-lock-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var repository = new ApplicationSettingsRepository(Path.Combine(root, "settings.json"));
            repository.MarkGoogleDriveRestorePending();
            using (new FileStream(
                       repository.GoogleDriveRestoreMarkerPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                Equal(true, repository.TryMarkGoogleDriveRestorePending(), "locked existing marker remains valid");
            }

            var invalidDirectory = Path.Combine(root, "not-a-directory");
            File.WriteAllText(invalidDirectory, "occupied", new UTF8Encoding(false));
            var unavailable = new ApplicationSettingsRepository(
                Path.Combine(invalidDirectory, "settings.json"));
            Equal(false, unavailable.TryMarkGoogleDriveRestorePending(), "unwritable marker is best-effort");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void ApplicationLifetimeRejectsSecondInstance()
    {
        var mutexName = "Local\\MSFSLandingStats.Application.Test." + Guid.NewGuid().ToString("N");
        Equal(true, ApplicationInstanceGuard.TryAcquire(mutexName, out var first), "first instance acquires lifetime guard");
        try
        {
            Equal(false, ApplicationInstanceGuard.TryAcquire(mutexName, out var second), "second instance is rejected");
            Null(second, "rejected instance does not retain a guard");
        }
        finally
        {
            first?.Dispose();
        }

        Equal(true, ApplicationInstanceGuard.TryAcquire(mutexName, out var replacement), "guard is released after exit");
        replacement?.Dispose();
    }

    private static void SimulatorAutoStartPreservesForeignBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-autostart-preserve-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "exe.xml");
        const string original = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                                "<SimBase.Document Type=\"Launch\" version=\"1,0\">  " +
                                "<Descr>Launch</Descr><!-- foreign comment -->" +
                                "<Filename>EXE.xml</Filename><Disabled>False</Disabled>" +
                                "<Launch.ManualLoad>False</Launch.ManualLoad>  " +
                                "<Launch.Addon><Name>FenixA320</Name><Disabled>false</Disabled>" +
                                "<Path>C:\\Program Files\\Fenix &amp; Friends\\Fenix.exe</Path></Launch.Addon>" +
                                "</SimBase.Document>";
        try
        {
            Directory.CreateDirectory(root);
            var originalBytes = new UTF8Encoding(false).GetBytes(original);
            File.WriteAllBytes(path, originalBytes);
            var manager = TestAutoStartManager(root, typeof(MainWindow).Assembly.Location);

            var enabled = manager.SetEnabled(true);
            Equal(1, enabled.ChangedPaths.Count, "first enable changes one profile");
            Equal(true, File.ReadAllText(path).Contains("MSFS Landing Stats: managed autostart begin"), "managed marker added");
            Equal(true, File.ReadAllText(path).Contains(typeof(MainWindow).Assembly.Location), "portable path added");
            Equal(true, File.ReadAllBytes(path + ".msfs-landing-stats.bak").SequenceEqual(originalBytes), "first backup is byte exact");

            var enabledBytes = File.ReadAllBytes(path);
            var secondEnable = manager.SetEnabled(true);
            Equal(0, secondEnable.ChangedPaths.Count, "second enable is a no-op");
            Equal(true, File.ReadAllBytes(path).SequenceEqual(enabledBytes), "no-op does not rewrite XML");

            var disabled = manager.SetEnabled(false);
            Equal(1, disabled.ChangedPaths.Count, "disable removes one managed entry");
            Equal(true, File.ReadAllBytes(path).SequenceEqual(originalBytes), "disable restores every original byte");
            Equal(false, Directory.EnumerateFiles(root, "*.tmp").Any(), "no temporary file remains");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void SimulatorAutoStartCreatesMissingConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-autostart-create-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "exe.xml");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "UserCfg.opt"), "InstalledPackagesPath x", new UTF8Encoding(false));
            var manager = TestAutoStartManager(root, typeof(MainWindow).Assembly.Location);
            manager.SetEnabled(true);
            var enabled = XDocument.Load(path);
            Equal("SimBase.Document", enabled.Root?.Name.LocalName, "created root");
            Equal(1, enabled.Root?.Elements("Launch.Addon").Count(), "created managed entry");

            manager.SetEnabled(false);
            var disabled = XDocument.Load(path);
            Equal(0, disabled.Root?.Elements("Launch.Addon").Count(), "managed entry removed from created file");
            Equal("Launch", disabled.Root?.Element("Descr")?.Value, "created base document remains valid");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void SimulatorAutoStartSupportsLegacyEncoding()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-autostart-encoding-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile");
        var applicationDirectory = Path.Combine(root, "приложение & тест");
        var applicationPath = Path.Combine(applicationDirectory, "Landing Stats.exe");
        var path = Path.Combine(profile, "exe.xml");
        try
        {
            Directory.CreateDirectory(profile);
            Directory.CreateDirectory(applicationDirectory);
            File.Copy(typeof(MainWindow).Assembly.Location, applicationPath);
            var windows1252 = Encoding.GetEncoding(1252);
            var original = "<?xml version=\"1.0\" encoding=\"Windows-1252\"?>" +
                           "<SimBase.Document Type=\"Launch\" version=\"1,0\"><Descr>Launch</Descr>" +
                           "<Filename>EXE.xml</Filename><Disabled>False</Disabled></SimBase.Document>";
            File.WriteAllBytes(path, windows1252.GetBytes(original));
            var manager = TestAutoStartManager(profile, applicationPath);

            manager.SetEnabled(true);
            var encodedText = windows1252.GetString(File.ReadAllBytes(path));
            Equal(true, encodedText.Contains("&#x"), "unrepresentable path characters use XML entities");
            var document = XDocument.Parse(encodedText);
            Equal(
                applicationPath,
                document.Root?.Elements("Launch.Addon").Single().Element("Path")?.Value,
                "Unicode path round-trips through Windows-1252 XML");

            manager.SetEnabled(false);
            Equal(original, windows1252.GetString(File.ReadAllBytes(path)), "legacy encoding restores exactly");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void SimulatorAutoStartPreservesUtf8Bom()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-autostart-bom-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "exe.xml");
        try
        {
            Directory.CreateDirectory(root);
            var encoding = new UTF8Encoding(true);
            var text = "<?xml version=\"1.0\" encoding=\"utf-8\"?><SimBase.Document Type=\"Launch\" version=\"1,0\"></SimBase.Document>";
            var original = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
            File.WriteAllBytes(path, original);
            var manager = TestAutoStartManager(root, typeof(MainWindow).Assembly.Location);
            manager.SetEnabled(true);
            Equal(true, File.ReadAllBytes(path).Take(3).SequenceEqual(encoding.GetPreamble()), "UTF-8 BOM remains present");
            manager.SetEnabled(false);
            Equal(true, File.ReadAllBytes(path).SequenceEqual(original), "UTF-8 BOM file restores exactly");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void SimulatorAutoStartRefusesMalformedConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-autostart-malformed-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "exe.xml");
        try
        {
            Directory.CreateDirectory(root);
            var original = new UTF8Encoding(false).GetBytes("<SimBase.Document><broken></SimBase.Document>");
            File.WriteAllBytes(path, original);
            var manager = TestAutoStartManager(root, typeof(MainWindow).Assembly.Location);
            Throws<InvalidDataException>(() => manager.SetEnabled(true), "malformed XML is rejected");
            Equal(true, File.ReadAllBytes(path).SequenceEqual(original), "malformed XML remains untouched");
            Equal(false, File.Exists(path + ".msfs-landing-stats.bak"), "failure does not create a misleading backup");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void SimulatorAutoStartLeavesUnmanagedEntryAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-autostart-unmanaged-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "exe.xml");
        var original = "<?xml version=\"1.0\"?><SimBase.Document Type=\"Launch\" version=\"1,0\">" +
                       "<Launch.Addon><Name>MSFS Landing Stats</Name><Disabled>False</Disabled>" +
                       "<Path>C:\\manual\\landing.exe</Path></Launch.Addon></SimBase.Document>";
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, original, new UTF8Encoding(false));
            var manager = TestAutoStartManager(root, typeof(MainWindow).Assembly.Location);
            manager.SetEnabled(false);
            Equal(original, File.ReadAllText(path), "disabling does not claim an unmanaged entry");
            Throws<InvalidDataException>(() => manager.SetEnabled(true), "enabling refuses an unmanaged name collision");
            Equal(original, File.ReadAllText(path), "name collision remains untouched");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void SimulatorAutoStartIsTransactionalAcrossProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-autostart-transaction-" + Guid.NewGuid().ToString("N"));
        var validRoot = Path.Combine(root, "valid");
        var invalidRoot = Path.Combine(root, "invalid");
        var validPath = Path.Combine(validRoot, "exe.xml");
        var invalidPath = Path.Combine(invalidRoot, "exe.xml");
        const string valid = "<?xml version=\"1.0\"?><SimBase.Document Type=\"Launch\" version=\"1,0\"></SimBase.Document>";
        const string invalid = "<SimBase.Document>";
        try
        {
            Directory.CreateDirectory(validRoot);
            Directory.CreateDirectory(invalidRoot);
            File.WriteAllText(validPath, valid, new UTF8Encoding(false));
            File.WriteAllText(invalidPath, invalid, new UTF8Encoding(false));
            var profiles = new[]
            {
                new SimulatorAutoStartManager.SimulatorProfile("valid", validRoot),
                new SimulatorAutoStartManager.SimulatorProfile("invalid", invalidRoot),
            };
            var manager = new SimulatorAutoStartManager(profiles, () => typeof(MainWindow).Assembly.Location);
            Throws<InvalidDataException>(() => manager.SetEnabled(true), "one malformed profile aborts the transaction");
            Equal(valid, File.ReadAllText(validPath), "prepared valid profile was never committed");
            Equal(invalid, File.ReadAllText(invalidPath), "invalid profile remains untouched");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void SimulatorAutoStartDiscoversKnownProfiles()
    {
        var profiles = SimulatorAutoStartManager.DefaultProfiles("R:\\Roaming", "L:\\Local");
        Equal(2, profiles.Count, "supported profile count");
        Equal(
            Path.Combine("R:\\Roaming", "Microsoft Flight Simulator 2024", "exe.xml"),
            profiles[0].ExeXmlPath,
            "MSFS 2024 Steam path");
        Equal(
            Path.Combine("L:\\Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "exe.xml"),
            profiles[1].ExeXmlPath,
            "MSFS 2024 Store path");
        Equal(false, profiles.Any(profile => profile.Name.Contains("2020")), "MSFS 2020 is not a supported profile");
    }

    private static void SimulatorAutoStartCleansUnsupported2020Entry()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-autostart-msfs2020-cleanup-" + Guid.NewGuid().ToString("N"));
        var supportedRoot = Path.Combine(root, "supported-2024");
        var legacyRoot = Path.Combine(root, "unsupported-2020");
        var supportedPath = Path.Combine(supportedRoot, "exe.xml");
        var legacyPath = Path.Combine(legacyRoot, "exe.xml");
        const string original = "<?xml version=\"1.0\"?><SimBase.Document Type=\"Launch\" version=\"1,0\">" +
                                "<Launch.Addon><Name>Foreign add-on</Name><Path>C:\\foreign.exe</Path></Launch.Addon>" +
                                "</SimBase.Document>";
        try
        {
            Directory.CreateDirectory(supportedRoot);
            Directory.CreateDirectory(legacyRoot);
            File.WriteAllText(supportedPath, original, new UTF8Encoding(false));
            File.WriteAllText(legacyPath, original, new UTF8Encoding(false));
            var supported = new SimulatorAutoStartManager.SimulatorProfile("MSFS 2024", supportedRoot);
            var legacy = new SimulatorAutoStartManager.SimulatorProfile("Unsupported MSFS 2020 cleanup", legacyRoot);
            var manager = new SimulatorAutoStartManager(
                new[] { supported },
                new[] { legacy },
                () => typeof(MainWindow).Assembly.Location);

            manager.SetEnabled(true);
            Equal(true, File.ReadAllText(supportedPath).Contains(SimulatorAutoStartManager.EntryName), "MSFS 2024 entry is enabled");
            Equal(original, File.ReadAllText(legacyPath), "MSFS 2020 is not modified while enabling");

            var v082Manager = new SimulatorAutoStartManager(new[] { legacy }, () => typeof(MainWindow).Assembly.Location);
            v082Manager.SetEnabled(true);
            Equal(true, File.ReadAllText(legacyPath).Contains("managed autostart begin"), "v0.8.2 legacy entry is simulated");

            var cleanup = manager.RemoveLegacyUnsupportedEntries();
            Equal(1, cleanup.ChangedPaths.Count, "legacy managed entry is removed");
            Equal(original, File.ReadAllText(legacyPath), "legacy file is restored byte for byte");

            manager.SetEnabled(false);
            Equal(original, File.ReadAllText(supportedPath), "supported file is restored byte for byte");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void SimulatorAutoStartRetainsLegacyCleanupError()
    {
        Equal(
            "legacy cleanup failed",
            MainWindow.CombineAutoStartErrors("legacy cleanup failed", null),
            "successful MSFS 2024 reconciliation does not hide cleanup failure");
        Equal(
            "legacy cleanup failed current update failed",
            MainWindow.CombineAutoStartErrors("legacy cleanup failed", "current update failed"),
            "cleanup and current errors are both retained");
        Null(MainWindow.CombineAutoStartErrors(null, null), "no failure remains empty");
    }

    private static SimulatorAutoStartManager TestAutoStartManager(string profileRoot, string applicationPath)
    {
        return new SimulatorAutoStartManager(
            new[] { new SimulatorAutoStartManager.SimulatorProfile("test", profileRoot) },
            () => applicationPath);
    }

    private static void WpfResourcesSwitchCompletely()
    {
        LocalizationManager.Apply("en");
        Equal("SETTINGS", System.Windows.Application.Current.TryFindResource("Settings.Title"), "English WPF resource");

        LocalizationManager.Apply("ru");
        Equal("НАСТРОЙКИ", System.Windows.Application.Current.TryFindResource("Settings.Title"), "Russian WPF resource");
        Equal("Landing Stats", System.Windows.Application.Current.TryFindResource("Product.Name"), "application brand is not translated");
        Equal(
            "Средняя вертикальная скорость за месяц",
            System.Windows.Application.Current.TryFindResource("History.AverageRate"),
            "Russian history resource");

        var localizationDictionaries = System.Windows.Application.Current.Resources.MergedDictionaries.Count(
            dictionary => dictionary.Source?.OriginalString.IndexOf(
                "Localization/Strings.",
                StringComparison.OrdinalIgnoreCase) >= 0);
        Equal(1, localizationDictionaries, "only one localization dictionary remains active");
        LocalizationManager.Apply("en");
    }

    private static void LandingFeelSeverityIsLanguageIndependent()
    {
        var record = new LandingRecord { InertialFpm = 300.0 };
        LocalizationManager.Apply("ru");
        Equal("ЖЁСТКАЯ", record.LandingFeelDisplay, "Russian firm label");
        Equal(true, record.IsFirmLanding, "firm severity flag");

        record.InertialFpm = 200.0;
        Equal("МЯГКАЯ", record.LandingFeelDisplay, "Russian smooth label");
        Equal(false, record.IsFirmLanding, "smooth severity flag");
        LocalizationManager.Apply("en");
    }

    private static void LocalizedWhitespaceSurvivesWpfLoading()
    {
        LocalizationManager.Apply("en");
        Equal(true, LocalizationManager.Text("Delete.ContactSuffixFormat").StartsWith(" · ", StringComparison.Ordinal), "delete suffix leading space");
        Equal(true, LocalizationManager.Text("Update.StatusUpdatingFormat").StartsWith(" · ", StringComparison.Ordinal), "updating suffix leading space");
        Equal(true, LocalizationManager.Text("Update.StatusRejected").StartsWith(" · ", StringComparison.Ordinal), "rejected suffix leading space");
        Equal(true, LocalizationManager.Text("Model.GeometryQualityFormat").StartsWith(" · ", StringComparison.Ordinal), "geometry suffix leading space");
        Equal(true, LocalizationManager.Text("Footer.ReportBugHelp").StartsWith("Securely uploads", StringComparison.Ordinal), "bug-report scope remains explicit");
    }

    private static void TelemetryReceiverHeaderMatchesClient()
    {
        var serverHeader = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "schema-v5.header"),
            Encoding.UTF8).Trim();
        Equal(TelemetryCsv.Header, serverHeader, "server/client telemetry header");
    }

    private static void DeduplicatorKeepsLastFrame()
    {
        var first = new TelemetrySample { SimulationTimeSeconds = 10, Sequence = 1 };
        var replacement = new TelemetrySample { SimulationTimeSeconds = 10, Sequence = 2 };
        var later = new TelemetrySample { SimulationTimeSeconds = 10.02, Sequence = 3 };

        var result = TelemetryDeduplicator.Deduplicate(new[] { first, replacement, later });

        Equal(2, result.Count, "deduplicated frame count");
        Same(replacement, result[0], "latest same-time frame");
        Same(later, result[1], "later frame");
    }

    private static void ApproachTimeoutRearmsCapture()
    {
        using var recorder = NewRecorder();
        Invoke(recorder, "ProcessSample", ApproachSample(1));
        NotNull(Field(recorder, "_episodeSamples"), "episode should start below 500 ft");

        Invoke(recorder, "ProcessSample", ApproachSample(302));
        Null(Field(recorder, "_episodeSamples"), "timed-out episode should be discarded");
        Equal(true, Field(recorder, "_armed"), "recorder should be armed after timeout");

        Invoke(recorder, "ProcessSample", ApproachSample(302.02));
        NotNull(Field(recorder, "_episodeSamples"), "next descending frame should start a fresh episode");
    }

    private static void PausedFramesDoNotGrowEpisode()
    {
        using var recorder = NewRecorder();
        Invoke(recorder, "ProcessSample", ApproachSample(1));
        for (var index = 0; index < 10000; index++)
        {
            var paused = ApproachSample(1);
            paused.Sequence = index + 2;
            paused.HostElapsedSeconds = index * 0.02;
            Invoke(recorder, "ProcessSample", paused);
        }

        var samples = (ICollection)Field(recorder, "_episodeSamples")!;
        Equal(1, samples.Count, "one retained sample for a frozen simulator instant");
    }

    private static void PreRollUsesReceiptTimeAndRemainsBounded()
    {
        var buffer = new PreRollBuffer(15.0, 3);
        buffer.Add(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 100 }, 0.0);
        buffer.Add(new TelemetrySample { Sequence = 2, SimulationTimeSeconds = 100 }, 14.0);
        buffer.Add(new TelemetrySample { Sequence = 3, SimulationTimeSeconds = 0 }, 16.0);

        var afterFrozenAndFallbackTimes = buffer.ToArray();
        Equal(2, afterFrozenAndFallbackTimes.Length, "receipt-time window count");
        Equal(2L, afterFrozenAndFallbackTimes[0].Sequence, "frozen simulator time cannot retain stale head");

        for (var sequence = 4; sequence <= 20; sequence++)
        {
            buffer.Add(new TelemetrySample { Sequence = sequence, SimulationTimeSeconds = 0 }, 16.0);
        }

        var bounded = buffer.ToArray();
        Equal(3, bounded.Length, "hard sample limit");
        Equal(18L, bounded[0].Sequence, "hard limit retains newest samples");
        Equal(20L, bounded[2].Sequence, "hard limit retains tail");
    }

    private static void FullTelemetryGateHasHysteresis()
    {
        using var recorder = NewRecorder();
        Invoke(recorder, "SetFullTelemetryForAgl", 2900.0);
        Equal(true, Field(recorder, "_fullTelemetryEnabled"), "full telemetry below the enable gate");
        Invoke(recorder, "SetFullTelemetryForAgl", 3200.0);
        Equal(true, Field(recorder, "_fullTelemetryEnabled"), "hysteresis should retain full telemetry");
        Invoke(recorder, "SetFullTelemetryForAgl", 3600.0);
        Equal(false, Field(recorder, "_fullTelemetryEnabled"), "full telemetry above the disable gate");
        Invoke(recorder, "SetRawDebugEnabled", true);
        Equal(true, Field(recorder, "_fullTelemetryEnabled"), "RAW should override the altitude gate");
        Invoke(recorder, "SetRawDebugEnabled", false);
        Equal(false, Field(recorder, "_fullTelemetryEnabled"), "disabling RAW should restore the altitude gate");
    }

    private static void RawDebugStreamsIntoZip()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-raw-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new RawCaptureRepository(root);
            string? completedPath = null;
            using (var session = repository.StartCapture(
                       Array.Empty<TelemetrySample>(), "MSFS", "Test aircraft", "TEST", "TEST", Array.Empty<string>(), DateTime.UtcNow))
            {
                session.ChunkCompleted += (_, args) => completedPath = args.Path;
                session.Write(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 });
                session.Write(new TelemetrySample { Sequence = 2, SimulationTimeSeconds = 1.02 });
            }

            NotNull(completedPath, "completed RAW path");
            Equal(true, File.Exists(completedPath!), "RAW zip exists");
            using (var archive = ZipFile.OpenRead(completedPath!))
            using (var reader = new StreamReader(archive.GetEntry("telemetry.csv")!.Open()))
            {
                var lines = reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                Equal(3, lines.Length, "RAW header plus two samples");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void FullTelemetryToggleIsAbsent()
    {
        var field = typeof(MainWindow).GetField(
            "RawDebugToggle",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Equal<object?>(null, field, "main-window RAW toggle field");
    }

    private static void LandingEpisodeIdsPairStartAndCompletion()
    {
        using var disposable = NewRecorder();
        var recorder = (SimConnectLandingRecorder)disposable;
        long startedId = 0;
        long completedId = 0;
        recorder.EpisodeStarted += (_, args) => startedId = args.EpisodeId;
        recorder.EpisodeCompleted += (_, args) => completedId = args.EpisodeId;
        Invoke(recorder, "ProcessSample", ApproachSample(1));
        Invoke(recorder, "CompleteEpisode", false);
        Equal(true, startedId > 0, "positive episode id");
        Equal(startedId, completedId, "completed episode id");
    }

    private static void BugReportRetentionExpiresOnNextSequence()
    {
        var buffer = new LastLandingBugReportBuffer();
        var first = BugReportFixture(1, "first");
        buffer.BeginEpisode(1);
        Equal(true, buffer.TryRetain(first), "first report retained");
        NotNull(buffer.Available(), "first report available");

        buffer.BeginEpisode(2);
        Null(buffer.Available(), "previous report removed at next episode start");
        Equal(false, buffer.TryRetain(first), "late completion from previous episode rejected");

        var second = BugReportFixture(2, "second");
        Equal(true, buffer.TryRetain(second), "second report retained");
        buffer.MarkSubmitted(2);
        Null(buffer.Available(), "submitted report is no longer offered");
    }

    private static void BugReportArchiveContainsTelemetryAndResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-bug-report-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var candidate = BugReportFixture(17, "calculated-landing");
            var path = new BugReportRepository(root).Create(candidate, new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc));
            Equal(true, path.EndsWith("_bug_raw.zip", StringComparison.Ordinal), "bug report queue suffix");
            using var archive = ZipFile.OpenRead(path);
            var names = archive.Entries.Select(entry => entry.FullName).OrderBy(value => value).ToArray();
            Equal("landing-results.json,session.txt,telemetry.csv", string.Join(",", names), "bug report entries");
            using (var telemetry = new StreamReader(archive.GetEntry("telemetry.csv")!.Open()))
            {
                var rows = telemetry.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                Equal(2, rows.Length, "telemetry header plus retained frame");
            }
            using (var session = new StreamReader(archive.GetEntry("session.txt")!.Open()))
            {
                var text = session.ReadToEnd();
                Equal(true, text.Contains("capture_kind=bug_report"), "bug report capture kind");
                Equal(true, text.Contains("landing_count=1"), "bug report landing count");
            }
            using (var results = new StreamReader(archive.GetEntry("landing-results.json")!.Open()))
            {
                var text = results.ReadToEnd();
                Equal(true, text.Contains("calculated-landing"), "calculated landing payload");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void BugReportPersistsBeforeNetworkPreparation()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-offline-bug-report-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var candidate = BugReportFixture(23, "offline-landing");
            var buffer = new LastLandingBugReportBuffer();
            buffer.BeginEpisode(candidate.EpisodeId);
            Equal(true, buffer.TryRetain(candidate), "offline report retained before persistence");

            var path = MainWindow.PersistBugReport(
                new BugReportRepository(root),
                buffer,
                candidate,
                new DateTime(2026, 8, 21, 8, 0, 0, DateTimeKind.Utc));

            Equal(true, File.Exists(path), "offline report durable before any network operation");
            Null(buffer.Available(), "persisted offline report is single-use in the UI");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void WindowShutdownDrainsBugReportPersistence()
    {
        using var releaseWrite = new ManualResetEventSlim(false);
        var persistence = Task.Run(() => releaseWrite.Wait());
        var drainReturned = false;
        var drain = Task.Run(() =>
        {
            MainWindow.DrainBugReportPersistence(persistence);
            drainReturned = true;
        });

        Thread.Sleep(50);
        Equal(false, drainReturned, "shutdown waits while local report write is incomplete");
        releaseWrite.Set();
        Equal(true, drain.Wait(TimeSpan.FromSeconds(5)), "shutdown drain completes after local write");
        Equal(true, drainReturned, "shutdown resumes only after report durability boundary");
    }

    private static BugReportCandidate BugReportFixture(long episodeId, string landingId)
    {
        return new BugReportCandidate(
            episodeId,
            new[]
            {
                new TelemetrySample
                {
                    Sequence = episodeId,
                    SimulationTimeSeconds = episodeId,
                    MotionSimulation = true,
                },
            },
            "MSFS 2024",
            "Test aircraft",
            "TEST",
            "TEST",
            Array.Empty<string>(),
            new[]
            {
                new LandingRecord
                {
                    Id = landingId,
                    TimestampUtc = DateTime.UtcNow,
                    InertialFpm = -123,
                    SurfaceFpm = -120,
                    PeakG150Milliseconds = 1.2,
                },
            });
    }

    private static void RawCaptureCleansAbandonedTempChunks()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-raw-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var abandoned = Path.Combine(root, "old_raw.zip.tmp");
            var active = Path.Combine(root, "active_raw.zip.tmp");
            File.WriteAllText(abandoned, "old");
            File.WriteAllText(active, "active");
            File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));

            _ = new RawCaptureRepository(root);
            Equal(false, File.Exists(abandoned), "abandoned temporary chunk removed");
            Equal(true, File.Exists(active), "fresh temporary chunk retained");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void RawTelemetryQueueNeverBlocksProducer()
    {
        using var queue = new BoundedTelemetryQueue(1);
        Equal(true, queue.TryAdd(new TelemetrySample { Sequence = 1 }), "first queued sample");
        var stopwatch = Stopwatch.StartNew();
        Equal(false, queue.TryAdd(new TelemetrySample { Sequence = 2 }), "full queue rejects without dropping an accepted sample");
        stopwatch.Stop();
        Equal(true, stopwatch.Elapsed < TimeSpan.FromMilliseconds(100), "full queue returns immediately");
    }

    private static void TelemetryIdentityIsStableAndSigns()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-identity-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var endpoint = new Uri("https://telemetry.example.test/");
            var firstStore = new TelemetryUploadIdentityStore(root);
            var first = firstStore.Identity();
            var message = Encoding.ASCII.GetBytes("signed telemetry fixture");
            var signature = Convert.FromBase64String(first.Sign(message));
            using (var rsa = new RSACryptoServiceProvider { PersistKeyInCsp = false })
            {
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Convert.FromBase64String(first.PublicModulus),
                    Exponent = Convert.FromBase64String(first.PublicExponent),
                });
                Equal(true, rsa.VerifyData(message, CryptoConfig.MapNameToOID("SHA256"), signature), "identity signature");
            }

            firstStore.AcceptConsent();
            firstStore.MarkEnrolled(endpoint, true);
            var secondStore = new TelemetryUploadIdentityStore(root);
            var second = secondStore.Identity();
            Equal(first.InstallId, second.InstallId, "stable install id");
            Equal(first.PublicModulus, second.PublicModulus, "stable public key");
            Equal(true, secondStore.ConsentAccepted, "persisted consent");
            Equal(true, secondStore.IsEnrolled(endpoint), "persisted enrollment");
            var stored = File.ReadAllText(Path.Combine(root, "identity.json"));
            Equal(false, stored.Contains("<RSAKeyValue>"), "private key must not be stored as plaintext XML");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void UpdaterVerifiesSignedManifest()
    {
        const string manifest = "format=2\nversion=0.7.1\npackage=MSFS-Landing-Stats.zip\npackage-size=1\npackage-sha256=0000000000000000000000000000000000000000000000000000000000000000\nupdater=MSFS-Landing-Stats.Updater.exe\nupdater-size=1\nupdater-sha256=1111111111111111111111111111111111111111111111111111111111111111\n";
        const string signature = "c7UOGSyNQQxx5454aO2GCoPkhosnnvRrN9G0T3d8T1ZtRr2tK9lEHtCrF5iIS0zhSsQMwPctBKhnSBlajnMakvSbttgLQTrlV72eWO4JWB/gCciFZIs9uJYmTQxHaALJAdAiKP8huIdXgaeEENJvHQDSvcfv395T/ydSRKL4BFuKqnksCf7GrjUNuoGAnInmRG15NqdxvdsFkMYnenhOABM2G/NJ1ECRZ9LHB5fPUoEBHHYBzSROO6glbHQhW4tU8JR/X03acQ2WpZk57ty3fsBClkju4HO0FHAd6j2Huy/Szzj867MJMHuICRjVzUfkR7L+qnMTSdlWYsbLQSO7WLqYD0EmZ6i7T7axQrsqm3vt6Js6P+HWmtYntvOskgmlsPkiRxV+kdBEF9GkIiNuB4ox/9sW6yxBYvPli7z1qUXVXWZ86P39hlKiwJ8iCdi3/ipF5DV1VYnCMRmqTQsKnEs4a2eQvYMa1+tm4sf7HZXVbGPovwxAuvHo8oeuIb8U";
        var signedHandler = new UpdateFixtureHandler(manifest, signature);
        using (var updater = new ReleaseUpdater(signedHandler))
        {
            var result = updater.CheckAndInstallAsync(new Version(0, 7, 1), CancellationToken.None).GetAwaiter().GetResult();
            Equal(ReleaseUpdateState.Current, result.State, "signed manifest state");
            Equal(true, signedHandler.RequestPaths.Any(path => path.EndsWith("/update-channel.txt", StringComparison.Ordinal)), "current channel manifest request");
            Equal(true, signedHandler.RequestPaths.Any(path => path.EndsWith("/update-channel.sig", StringComparison.Ordinal)), "current channel signature request");
        }
        using (var updater = new ReleaseUpdater(new UpdateFixtureHandler(manifest.Replace("0.7.1", "9.9.9"), signature)))
        {
            var result = updater.CheckAndInstallAsync(new Version(0, 7, 1), CancellationToken.None).GetAwaiter().GetResult();
            Equal(ReleaseUpdateState.Rejected, result.State, "tampered manifest state");
        }
    }

    private static void TelemetryRegistrationIsAutomaticAndAnonymous()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-auto-enrollment-test-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");
            var handler = new TelemetryEnrollmentFixtureHandler();
            using var client = new TelemetryUploadClient(
                Path.Combine(root, "queue"),
                handler,
                Path.Combine(root, "identity"));
            var result = client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(TelemetryPreparationState.Ready, result.State, "automatic telemetry enrollment state");
            var refresh = client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(TelemetryPreparationState.Ready, refresh.State, "idempotent telemetry enrollment refresh");
            Equal(2, handler.EnrollmentCount, "server enrollment is refreshed for each report submission");
            var payload = handler.EnrollmentPayload ?? throw new InvalidOperationException("enrollment payload was not sent");
            Equal(true, payload.IndexOf("\"install_id\"", StringComparison.Ordinal) >= 0, "anonymous installation id field");
            Equal(true, payload.IndexOf("\"public_modulus\"", StringComparison.Ordinal) >= 0, "public key field");
            Equal(false, payload.IndexOf("invite", StringComparison.OrdinalIgnoreCase) >= 0, "invite field absence");
            Equal(false, payload.IndexOf("hardware", StringComparison.OrdinalIgnoreCase) >= 0, "hardware identifier absence");
            Equal(false, payload.IndexOf("machineguid", StringComparison.OrdinalIgnoreCase) >= 0, "MachineGuid absence");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void ExistingTelemetryWaitsForEnrollmentAndConsent()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-existing-upload-test-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            var queue = Path.Combine(root, "queue");
            Directory.CreateDirectory(queue);
            var legacy = Path.Combine(queue, "20260820_legacy_raw.zip");
            var report = Path.Combine(queue, "20260821_latest_bug_raw.zip");
            File.WriteAllBytes(legacy, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(report, new byte[] { 4, 5, 6 });
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");

            var handler = new TelemetryEnrollmentFixtureHandler();
            using var client = new TelemetryUploadClient(queue, handler, Path.Combine(root, "identity"));
            Thread.Sleep(100);
            Equal(0, handler.CaptureCount, "existing files are not touched before enrollment");

            var preparation = client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(TelemetryPreparationState.Ready, preparation.State, "existing queue enrollment");
            client.EnqueueExisting();
            Equal(
                true,
                SpinWait.SpinUntil(() => !File.Exists(report), TimeSpan.FromSeconds(5)),
                "user-initiated bug report uploaded without legacy RAW consent");
            Equal(true, File.Exists(legacy), "prior RAW refusal remains authoritative");
            Equal(1, handler.CaptureCount, "only bug report uploaded without legacy consent");

            client.AcceptConsent();
            client.EnqueueExisting();
            Equal(
                true,
                SpinWait.SpinUntil(() => !File.Exists(legacy), TimeSpan.FromSeconds(5)),
                "consented legacy RAW is retried");
            Equal(2, handler.CaptureCount, "legacy RAW uploaded after explicit consent");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void LegacyRawStartupRetryRequiresPersistedConsent()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-legacy-startup-test-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");

            var acceptedQueue = Path.Combine(root, "accepted-queue");
            var acceptedIdentity = Path.Combine(root, "accepted-identity");
            Directory.CreateDirectory(acceptedQueue);
            var acceptedRaw = Path.Combine(acceptedQueue, "legacy_raw.zip");
            File.WriteAllBytes(acceptedRaw, new byte[] { 1, 2, 3 });
            new TelemetryUploadIdentityStore(acceptedIdentity).AcceptConsent();
            var acceptedHandler = new TelemetryEnrollmentFixtureHandler();
            using (var client = new TelemetryUploadClient(acceptedQueue, acceptedHandler, acceptedIdentity))
            {
                Equal(true, client.HasEligiblePendingReports(), "consented legacy RAW is startup-eligible");
                client.PreparePendingReportsUntilReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
                Equal(
                    true,
                    SpinWait.SpinUntil(() => !File.Exists(acceptedRaw), TimeSpan.FromSeconds(5)),
                    "consented legacy RAW uploads after restart");
                Equal(1, acceptedHandler.EnrollmentCount, "consented startup performs enrollment");
            }

            var refusedQueue = Path.Combine(root, "refused-queue");
            var refusedIdentity = Path.Combine(root, "refused-identity");
            Directory.CreateDirectory(refusedQueue);
            var refusedRaw = Path.Combine(refusedQueue, "legacy_raw.zip");
            File.WriteAllBytes(refusedRaw, new byte[] { 4, 5, 6 });
            var refusedHandler = new TelemetryEnrollmentFixtureHandler();
            using (var client = new TelemetryUploadClient(refusedQueue, refusedHandler, refusedIdentity))
            {
                Equal(false, client.HasEligiblePendingReports(), "legacy RAW refusal remains startup-ineligible");
                client.PreparePendingReportsUntilReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
                Thread.Sleep(50);
                Equal(0, refusedHandler.EnrollmentCount, "refused startup performs no network enrollment");
                Equal(true, File.Exists(refusedRaw), "refused legacy RAW remains local");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void TelemetryByteLimitRejectsBeforeScheduling()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-upload-limit-test-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            var queue = Path.Combine(root, "queue");
            Directory.CreateDirectory(queue);
            var report = Path.Combine(queue, "oversized_bug_raw.zip");
            File.WriteAllBytes(report, new byte[] { 1, 2, 3, 4, 5 });
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");

            var handler = new TelemetryEnrollmentFixtureHandler();
            using var client = new TelemetryUploadClient(
                queue,
                handler,
                Path.Combine(root, "identity"),
                maximumQueueBytes: 4);
            var preparation = client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(TelemetryPreparationState.Ready, preparation.State, "queue-limit enrollment");
            Equal(false, client.Enqueue(report), "oversized capture rejected before enqueue");
            Thread.Sleep(100);
            Equal(0, handler.CaptureCount, "oversized capture never reaches upload worker");
            Equal(0L, (long)Field(client, "_queueBytes")!, "rejected capture reserves no queue bytes");
            Equal(true, File.Exists(report), "rejected capture remains durable on disk");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void TelemetryBacklogDrainsAfterSaturation()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-upload-drain-test-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            var queue = Path.Combine(root, "queue");
            Directory.CreateDirectory(queue);
            var reports = Enumerable.Range(1, 3)
                .Select(index => Path.Combine(queue, $"20260821_00000{index}_bug_raw.zip"))
                .ToArray();
            foreach (var report in reports)
            {
                File.WriteAllBytes(report, new byte[] { 1, 2, 3 });
            }
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");

            var handler = new TelemetryEnrollmentFixtureHandler();
            using var client = new TelemetryUploadClient(
                queue,
                handler,
                Path.Combine(root, "identity"),
                maximumQueueBytes: 4,
                maximumQueuedFiles: 1);
            var preparation = client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(TelemetryPreparationState.Ready, preparation.State, "saturated backlog enrollment");
            client.EnqueueExisting();

            Equal(
                true,
                SpinWait.SpinUntil(
                    () => reports.All(report => !File.Exists(report)) &&
                          (long)Field(client, "_queueBytes")! == 0,
                    TimeSpan.FromSeconds(5)),
                "all durable reports drain and release byte and file capacity");
            Equal(3, handler.CaptureCount, "each saturated report uploaded exactly once");
            Equal(0L, (long)Field(client, "_queueBytes")!, "drained backlog releases queue bytes");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void PendingBugReportRecoversAfterEnrollmentFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-enrollment-recovery-test-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            var queue = Path.Combine(root, "queue");
            Directory.CreateDirectory(queue);
            var report = Path.Combine(queue, "offline_bug_raw.zip");
            File.WriteAllBytes(report, new byte[] { 1, 2, 3 });
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");

            var handler = new TelemetryRecoveringFixtureHandler(enrollmentFailures: 1);
            using var client = new TelemetryUploadClient(
                queue,
                handler,
                Path.Combine(root, "identity"),
                retryDelay: TimeSpan.FromMilliseconds(1));
            Equal(true, TelemetryUploadClient.HasPendingBugReports(queue), "restart detects durable bug report");
            client.PreparePendingReportsUntilReadyAsync(
                    CancellationToken.None,
                    TimeSpan.FromMilliseconds(1))
                .GetAwaiter().GetResult();

            Equal(
                true,
                SpinWait.SpinUntil(() => !File.Exists(report), TimeSpan.FromSeconds(5)),
                "pending report uploaded after enrollment recovers");
            Equal(2, handler.EnrollmentCount, "failed enrollment is retried");
            Equal(1, handler.CaptureCount, "recovered report uploaded once");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void OversizedBacklogEntryDoesNotStarveValidReport()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-oversized-backlog-test-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            var queue = Path.Combine(root, "queue");
            Directory.CreateDirectory(queue);
            var oversized = Path.Combine(queue, "20260820_old_bug_raw.zip");
            var valid = Path.Combine(queue, "20260821_new_bug_raw.zip");
            File.WriteAllBytes(oversized, new byte[] { 1, 2, 3, 4, 5 });
            File.WriteAllBytes(valid, new byte[] { 6, 7, 8 });
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");

            var handler = new TelemetryEnrollmentFixtureHandler();
            using var client = new TelemetryUploadClient(
                queue,
                handler,
                Path.Combine(root, "identity"),
                maximumQueueBytes: 4);
            Equal(
                TelemetryPreparationState.Ready,
                client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult().State,
                "oversized backlog enrollment");
            client.EnqueueExisting();

            Equal(
                true,
                SpinWait.SpinUntil(() => !File.Exists(valid), TimeSpan.FromSeconds(5)),
                "valid report behind oversized item is uploaded");
            Equal(true, File.Exists(oversized), "oversized report remains on disk for inspection");
            Equal(1, handler.CaptureCount, "only valid report reaches uploader");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void UploadReenrollsAfterForbiddenResponse()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-reenroll-test-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            var queue = Path.Combine(root, "queue");
            Directory.CreateDirectory(queue);
            var report = Path.Combine(queue, "reenroll_bug_raw.zip");
            File.WriteAllBytes(report, new byte[] { 1, 2, 3 });
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");

            var handler = new TelemetryRecoveringFixtureHandler(forbidFirstCapture: true);
            using var client = new TelemetryUploadClient(
                queue,
                handler,
                Path.Combine(root, "identity"),
                retryDelay: TimeSpan.FromMilliseconds(1));
            Equal(
                TelemetryPreparationState.Ready,
                client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult().State,
                "initial enrollment before forbidden response");
            Equal(true, client.Enqueue(report), "report queued before enrollment invalidation");

            Equal(
                true,
                SpinWait.SpinUntil(() => !File.Exists(report), TimeSpan.FromSeconds(5)),
                "report uploaded after automatic re-enrollment");
            Equal(2, handler.EnrollmentCount, "403 triggers a fresh enrollment");
            Equal(2, handler.CaptureCount, "capture retried once after 403");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void UploadWorkerSurvivesReadOnlyQueueFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-readonly-upload-test-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            var queue = Path.Combine(root, "queue");
            Directory.CreateDirectory(queue);
            var report = Path.Combine(queue, "readonly_bug_raw.zip");
            File.WriteAllBytes(report, new byte[] { 1, 2, 3 });
            File.SetAttributes(report, FileAttributes.ReadOnly);
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");

            var handler = new TelemetryEnrollmentFixtureHandler();
            using var client = new TelemetryUploadClient(
                queue,
                handler,
                Path.Combine(root, "identity"),
                retryDelay: TimeSpan.FromMilliseconds(5));
            Equal(
                TelemetryPreparationState.Ready,
                client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult().State,
                "read-only queue enrollment");
            Equal(true, client.Enqueue(report), "read-only report queued");
            Equal(
                true,
                SpinWait.SpinUntil(() => handler.CaptureCount > 0, TimeSpan.FromSeconds(5)),
                "read-only report reaches receiver");
            var worker = (Task)Field(client, "_worker")!;
            Equal(false, worker.IsFaulted, "delete ACL error does not fault worker");

            File.SetAttributes(report, FileAttributes.Normal);
            Equal(
                true,
                SpinWait.SpinUntil(() => !File.Exists(report), TimeSpan.FromSeconds(5)),
                "read-only report retries after filesystem access recovers");
            Equal(false, worker.IsFaulted, "worker remains live after retry");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root))
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
                Directory.Delete(root, true);
            }
        }
    }

    private static void TelemetryEnqueueIsSafeDuringDisposal()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-dispose-race-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            Directory.CreateDirectory(root);
            var queue = Path.Combine(root, "queue");
            Directory.CreateDirectory(queue);
            var capture = Path.Combine(queue, "race_raw.zip");
            File.WriteAllBytes(capture, new byte[] { 1, 2, 3 });
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");
            var client = new TelemetryUploadClient(queue, new TelemetryEnrollmentFixtureHandler(), Path.Combine(root, "identity"));
            Exception? producerFailure = null;
            var producer = new Thread(() =>
            {
                try
                {
                    for (var index = 0; index < 5000; index++)
                    {
                        client.Enqueue(capture);
                    }
                }
                catch (Exception exception)
                {
                    producerFailure = exception;
                }
            });
            producer.Start();
            client.Dispose();
            producer.Join();
            Null(producerFailure, "enqueue/dispose race exception");
            Equal(false, client.Enqueue(capture), "enqueue after disposal");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void PermanentlyRejectedTelemetryIsQuarantined()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-rejected-upload-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            var queue = Path.Combine(root, "queue");
            Directory.CreateDirectory(queue);
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");
            var handler = new TelemetryRejectingFixtureHandler();
            using var client = new TelemetryUploadClient(queue, handler, Path.Combine(root, "identity"));
            var preparation = client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(TelemetryPreparationState.Ready, preparation.State, "rejection fixture enrollment");

            var capture = Path.Combine(queue, "permanent_raw.zip");
            File.WriteAllBytes(capture, Enumerable.Repeat((byte)0x5A, 64).ToArray());
            Equal(true, client.Enqueue(capture), "capture accepted into upload queue");
            Equal(
                true,
                SpinWait.SpinUntil(
                    () => Directory.EnumerateFiles(queue, "*.rejected-422.zip").Any(),
                    TimeSpan.FromSeconds(5)),
                "permanent rejection quarantine");
            Thread.Sleep(150);
            Equal(1, handler.CaptureCount, "permanently rejected capture is not retried");
            Equal(false, File.Exists(capture), "rejected raw queue name removed");
            Equal(0L, (long)Field(client, "_queueBytes")!, "quarantined bytes leave queue accounting");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void CorruptTelemetryIdentityDoesNotFaultWorker()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-corrupt-identity-" + Guid.NewGuid().ToString("N"));
        var previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            var queue = Path.Combine(root, "queue");
            var identity = Path.Combine(root, "identity");
            Directory.CreateDirectory(queue);
            Directory.CreateDirectory(identity);
            File.WriteAllText(Path.Combine(identity, "identity.json"), "{\"install_id\":\"damaged\"}");
            File.WriteAllBytes(Path.Combine(queue, "corrupt_identity_raw.zip"), new byte[] { 1, 2, 3 });
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");

            using var client = new TelemetryUploadClient(queue, new TelemetryEnrollmentFixtureHandler(), identity);
            var preparation = client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(TelemetryPreparationState.Unavailable, preparation.State, "corrupt identity preparation state");
            Thread.Sleep(100);
            var worker = (Task)Field(client, "_worker")!;
            Equal(false, worker.IsFaulted, "upload worker remains alive");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void UpdaterCleanupRefusesUnsafePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-stats-unsafe-cleanup-test-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(directory, "keep.txt");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(marker, "keep");
            ReleaseUpdater.BeginCompletedUpdateCleanup(new[] { "app", "--finish-update", "1", directory });
            Equal(true, File.Exists(marker), "unsafe cleanup marker");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static void UpdaterAcceptsSingleExecutableManifestShape()
    {
        const string manifest = "format=3\nversion=0.7.3\npackage=MSFS-Landing-Stats.exe\npackage-size=488021\npackage-sha256=0000000000000000000000000000000000000000000000000000000000000000\nupdater=MSFS-Landing-Stats.Updater.exe\nupdater-size=113152\nupdater-sha256=1111111111111111111111111111111111111111111111111111111111111111\n";
        var protocolType = typeof(ReleaseUpdater).Assembly.GetType(
            "LandingStats.UpdateProtocol.ReleaseUpdateProtocol",
            true)!;
        var parse = protocolType.GetMethod("ParseManifest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var parsed = parse.Invoke(null, new object[] { new UTF8Encoding(false).GetBytes(manifest) })!;
        var packageAsset = parsed.GetType().GetProperty("PackageAsset")!.GetValue(parsed) as string;
        Equal("MSFS-Landing-Stats.exe", packageAsset, "format-3 package asset");
    }

    private static void UpdaterAcceptsIssuedManifestShape()
    {
        const string manifest = "format=2\nversion=0.7.5\npackage=MSFS-Landing-Stats.zip\npackage-size=488021\npackage-sha256=0000000000000000000000000000000000000000000000000000000000000000\nupdater=MSFS-Landing-Stats.Updater.exe\nupdater-size=113152\nupdater-sha256=1111111111111111111111111111111111111111111111111111111111111111\n";
        var protocolType = typeof(ReleaseUpdater).Assembly.GetType(
            "LandingStats.UpdateProtocol.ReleaseUpdateProtocol",
            true)!;
        var parse = protocolType.GetMethod("ParseManifest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var parsed = parse.Invoke(null, new object[] { new UTF8Encoding(false).GetBytes(manifest) })!;
        var packageAsset = parsed.GetType().GetProperty("PackageAsset")!.GetValue(parsed) as string;
        Equal("MSFS-Landing-Stats.zip", packageAsset, "format-2 package asset");
    }

    private static void UpdaterPreservesManifestChannels()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-update-channel-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, "MSFS-Landing-Stats.exe");
            File.WriteAllText(target, "fixture");
            var readyEventName = "Local\\MSFSLandingStatsUpdate-" + Guid.NewGuid().ToString("N");
            var parse = typeof(UpdaterProgram).GetMethod("ParseInvocation", BindingFlags.Static | BindingFlags.NonPublic)!;
            var common = new[]
            {
                "--apply", "--parent-pid", "1", "--target", target,
                "--version", "0.7.6", "--ready-event", readyEventName,
            };

            var bridge = parse.Invoke(null, new object[] { common })!;
            Equal("update-manifest.txt", bridge.GetType().GetProperty("ManifestName")!.GetValue(bridge), "legacy bridge manifest default");

            var channelArgs = common.Concat(new[] { "--manifest", "update-channel.txt" }).ToArray();
            var current = parse.Invoke(null, new object[] { channelArgs })!;
            Equal("update-channel.txt", current.GetType().GetProperty("ManifestName")!.GetValue(current), "current manifest channel");

            var rejected = false;
            try
            {
                parse.Invoke(null, new object[] { common.Concat(new[] { "--manifest", "evil.txt" }).ToArray() });
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
            {
                rejected = true;
            }
            Equal(true, rejected, "unknown manifest channel rejected");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void UpdaterAcceptsBrowserRenamedTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-renamed-update-test-" + Guid.NewGuid().ToString("N"));
        var previousLauncherPath = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_LAUNCHER_PATH");
        try
        {
            Directory.CreateDirectory(root);
            var resolve = typeof(ReleaseUpdater).GetMethod("ResolveUpdateTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
            var parse = typeof(UpdaterProgram).GetMethod("ParseInvocation", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var fileName in new[]
            {
                "MSFS-Landing-Stats (2).exe",
                "Landing copy (4).EXE",
                "моя посадка.exe",
            })
            {
                var target = Path.Combine(root, fileName);
                File.WriteAllText(target, "fixture");
                Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_LAUNCHER_PATH", target);
                Equal(target, resolve.Invoke(null, null), $"application accepts target {fileName}");

                var readyEventName = "Local\\MSFSLandingStatsUpdate-" + Guid.NewGuid().ToString("N");
                var invocation = parse.Invoke(null, new object[]
                {
                    new[]
                    {
                        "--apply", "--parent-pid", "1", "--target", target,
                        "--version", "0.8.0", "--ready-event", readyEventName,
                        "--manifest", "update-channel.txt",
                    },
                })!;
                Equal(target, invocation.GetType().GetProperty("TargetPath")!.GetValue(invocation), $"external updater accepts target {fileName}");
            }

            var nonExecutable = Path.Combine(root, "MSFS-Landing-Stats (2).dll");
            File.WriteAllText(nonExecutable, "fixture");
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_LAUNCHER_PATH", nonExecutable);
            var rejected = false;
            try
            {
                resolve.Invoke(null, null);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
            {
                rejected = true;
            }
            Equal(true, rejected, "application rejects non-executable target");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_LAUNCHER_PATH", previousLauncherPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void UpdaterExtractsLegacySingleExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-legacy-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var source = Path.Combine(root, "source.exe");
            var package = Path.Combine(root, "MSFS-Landing-Stats.zip");
            var destination = Path.Combine(root, "MSFS-Landing-Stats.exe");
            File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("MSFS-Landing-Stats.exe", CompressionLevel.NoCompression);
                using var input = File.OpenRead(source);
                using var output = entry.Open();
                input.CopyTo(output);
            }

            UpdaterProgram.ExtractLegacySingleExecutable(package, destination);
            Equal("01020304", BitConverter.ToString(File.ReadAllBytes(destination)).Replace("-", string.Empty), "legacy package payload");

            File.Delete(destination);
            File.Delete(package);
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                archive.CreateEntry("MSFS-Landing-Stats.exe");
                archive.CreateEntry("unexpected.dll");
            }
            var rejected = false;
            try
            {
                UpdaterProgram.ExtractLegacySingleExecutable(package, destination);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Equal(true, rejected, "legacy package rejects extra entry");
            Equal(false, File.Exists(destination), "rejected legacy package leaves no executable");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void UpdaterInstallsSingleExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-single-update-test-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "arbitrary downloaded name (4).exe");
        var replacement = Path.Combine(root, "replacement.exe");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(target, "old");
            var search = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string? builtExecutable = null;
            while (search != null && builtExecutable == null)
            {
                var candidate = Path.Combine(search.FullName, "artifacts", "MSFS-Landing-Stats.exe");
                if (File.Exists(candidate))
                {
                    builtExecutable = candidate;
                }
                search = search.Parent;
            }
            if (builtExecutable == null) throw new FileNotFoundException("Built single executable fixture is unavailable");
            File.Copy(builtExecutable, replacement);

            var replacementVersion = AssemblyName.GetAssemblyName(replacement).Version
                                     ?? throw new InvalidDataException("replacement fixture version is unavailable");
            UpdaterProgram.InstallExecutableTransactionally(replacement, target, replacementVersion);
            Equal(false, File.Exists(replacement), "replacement moved into place");
            Equal(true, new FileInfo(target).Length > 100, "installed executable length");
            Equal(0, Directory.GetFiles(root, ".msfs-landing-stats-backup-*.exe").Length, "transaction backup removed");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void UpdaterRollsBackInvalidExecutableReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-update-rollback-test-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "MSFS-Landing-Stats.exe");
        var replacement = Path.Combine(root, "replacement.exe");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(target, "known-good-old-executable");
            File.WriteAllText(replacement, "invalid-new-executable");

            var failed = false;
            try
            {
                UpdaterProgram.InstallExecutableTransactionally(replacement, target, new Version(0, 7, 3));
            }
            catch (InvalidDataException)
            {
                failed = true;
            }
            Equal(true, failed, "invalid executable transaction failure");
            Equal("known-good-old-executable", File.ReadAllText(target), "single executable rollback content");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void ValidFrameClearsTransientErrorState()
    {
        using var recorder = NewRecorder();
        SetField(recorder, "_recoverStatusAfterFrame", true);
        Invoke(recorder, "RecoverStatusAfterFrame");
        Equal(false, Field(recorder, "_recoverStatusAfterFrame"), "transient error recovery flag");
    }

    private static void ReplayFlowEventsDisableCapture()
    {
        using var recorder = NewRecorder();
        Invoke(recorder, "ProcessSample", ApproachSample(1));
        NotNull(Field(recorder, "_episodeSamples"), "episode before replay");

        Invoke(
            recorder,
            "OnRecvFlowEvent",
            null!,
            ReplayFlowEvent("REPLAY_START"));
        Equal(true, Field(recorder, "_replayActive"), "replay active flag");
        Equal(false, Field(recorder, "_armed"), "capture disarmed during replay");
        Null(Field(recorder, "_episodeSamples"), "approach discarded at replay start");

        Invoke(recorder, "ProcessSample", ApproachSample(2));
        Null(Field(recorder, "_episodeSamples"), "replay frame cannot start an episode");

        Invoke(
            recorder,
            "OnRecvFlowEvent",
            null!,
            ReplayFlowEvent("REPLAY_END"));
        Equal(false, Field(recorder, "_replayActive"), "replay cleared flag");
        Equal(true, Field(recorder, "_armed"), "capture re-armed after replay");

        Invoke(recorder, "ProcessSample", ApproachSample(3));
        NotNull(Field(recorder, "_episodeSamples"), "normal capture resumes after replay");
    }

    private static void RecorderReportsAirborneState()
    {
        using var recorder = NewRecorder();
        NotNull(RecorderType().GetEvent("AircraftGroundStateChanged"), "aircraft ground-state event");

        Invoke(recorder, "ProcessSample", new TelemetrySample
        {
            SimulationTimeSeconds = 1.0,
            OnGround = true,
            AboveGroundLevelFeet = 0.0,
        });
        Invoke(recorder, "ProcessSample", new TelemetrySample
        {
            SimulationTimeSeconds = 2.0,
            OnGround = false,
            AboveGroundLevelFeet = 50.0,
        });

        Equal(false, Field(recorder, "_aircraftOnGround"), "latest tracked state is airborne");
        Equal(
            true,
            RecorderType().GetProperty("IsAircraftAirborne")!.GetValue(recorder),
            "airborne state exposed to overlay");
    }

    private static void ReplayFramesDoNotAlterLiveStateOrRawTelemetry()
    {
        using var recorder = NewRecorder();
        var rawFrames = 0;
        EventHandler<RawDebugSampleEventArgs> rawHandler = (_, _) => rawFrames++;
        RecorderType().GetEvent("RawDebugSampleReceived")!.AddEventHandler(recorder, rawHandler);
        SetField(recorder, "_rawDebugEnabled", true);

        Invoke(recorder, "ProcessSample", new TelemetrySample
        {
            SimulationTimeSeconds = 1.0,
            OnGround = true,
        });
        Equal(1, rawFrames, "live frame enters raw stream");
        Equal(true, Field(recorder, "_aircraftOnGround"), "live ground state before replay");

        Invoke(recorder, "OnRecvFlowEvent", null!, ReplayFlowEvent("REPLAY_START"));
        Invoke(recorder, "ProcessSample", new TelemetrySample
        {
            SimulationTimeSeconds = 2.0,
            OnGround = false,
            AboveGroundLevelFeet = 100.0,
        });

        Equal(1, rawFrames, "replay frame excluded from raw stream");
        Equal(true, Field(recorder, "_aircraftOnGround"), "replay frame cannot replace live ground state");
    }

    private static void ReplayKinematicInconsistenciesAreRejected()
    {
        Equal(false, ReplayTelemetryDetector.IsReplayLike(ReplayDetectionSamples(false)), "consistent flight telemetry");
        Equal(true, ReplayTelemetryDetector.IsReplayLike(ReplayDetectionSamples(true)), "replayed pose with inert telemetry");

        var stationary = ReplayDetectionSamples(true);
        foreach (var sample in stationary)
        {
            sample.LatitudeDegrees = 42.0;
            sample.LongitudeDegrees = 23.0;
        }

        Equal(false, ReplayTelemetryDetector.IsReplayLike(stationary), "stationary aircraft is not replay evidence");
    }

    private static void LegacyControllerPathIsAbsent()
    {
        var type = RecorderType();
        Null(type.GetMethod("OnRecvControllersList", InstancePrivate), "legacy controller-list handler");
        Null(type.GetNestedType("InputGroups", BindingFlags.NonPublic), "legacy input-group enum");
    }

    private static void CompactFrameMatchesPayloadContract()
    {
        var frameType = typeof(MainWindow).Assembly.GetType(
                            "LandingStats.App.Telemetry.SimFrameData",
                            true)
                        ?? throw new TypeLoadException("SimFrameData");
        Equal(20, TelemetrySample.CapturedContactPointCount, "documented contact-point count");
        Equal(4, TelemetrySample.CapturedEngineCount, "documented SimConnect engine count");
        Equal(892, Marshal.SizeOf(frameType), "packed frame size");
        var guardType = typeof(MainWindow).Assembly.GetType(
                            "LandingStats.App.Telemetry.SimGuardData",
                            true)
                        ?? throw new TypeLoadException("SimGuardData");
        Equal(32, Marshal.SizeOf(guardType), "packed guard size");
    }

    private static void TelemetrySchemaReadsCurrentAndV4Rows()
    {
        var sample = new TelemetrySample
        {
            Sequence = 42,
            SimulationTimeSeconds = 123.5,
            MotionSimulation = true,
            GroundAltitudeFeet = 1740.25,
        };
        sample.ContactPointCompression[19] = 0.375;
        sample.ContactPointPosition[19] = 0.875;
        sample.ContactPointOnGround[19] = true;

        var currentLine = TelemetryCsv.Format(sample);
        Equal(5, TelemetryCsv.SchemaVersion, "current telemetry schema");
        Equal(160, TelemetryCsv.Header.Split(',').Length, "compact telemetry columns");
        Equal(325, TelemetryCsv.V4Header.Split(',').Length, "v4 telemetry columns");
        Equal(TelemetryCsv.Header.Split(',').Length, currentLine.Split(',').Length, "header and row column count");
        Equal(true, TelemetryCsv.TryParse(currentLine, out var current), "parse current telemetry row");
        Equal(0.375, current.ContactPointCompression[19], "current contact compression");
        Equal(true, current.ContactPointOnGround[19], "current contact state");

        var currentColumns = TelemetryCsv.Header.Split(',');
        var currentValues = currentLine.Split(',');
        var valuesByColumn = currentColumns
            .Select((column, index) => new { column, value = currentValues[index] })
            .ToDictionary(item => item.column, item => item.value, StringComparer.Ordinal);
        var v4Line = string.Join(",", TelemetryCsv.V4Header.Split(',').Select(column =>
            valuesByColumn.TryGetValue(column, out var value) ? value : "0"));

        Equal(true, TelemetryCsv.TryParse(v4Line, out var v4), "parse v4 telemetry row");
        Equal(0.375, v4.ContactPointCompression[19], "v4 contact compression");
        Equal(true, v4.ContactPointOnGround[19], "v4 contact state");
    }

    private static void TelemetryCsvIsStrictAndRoundTrips()
    {
        var value = BitConverter.Int64BitsToDouble(0x3FD5555555555555);
        var sample = new TelemetrySample
        {
            Sequence = 7,
            HostElapsedSeconds = value,
            SimulationTimeSeconds = 10.0,
        };
        var line = TelemetryCsv.Format(sample);
        Equal(true, TelemetryCsv.TryParse(line, out var parsed), "round-trip row parse");
        Equal(
            BitConverter.DoubleToInt64Bits(value),
            BitConverter.DoubleToInt64Bits(parsed.HostElapsedSeconds),
            "binary64 round trip");

        var root = Path.Combine(Path.GetTempPath(), "landing-stats-csv-schema-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "truncated.csv");
            var truncated = string.Join(",", line.Split(',').Take(22));
            File.WriteAllText(path, TelemetryCsv.Header + Environment.NewLine + truncated + Environment.NewLine);
            var rejected = false;
            try
            {
                TelemetryCsv.ReadFile(path);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            Equal(true, rejected, "current header with legacy-width row is rejected");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void HeaderWindUsesContactTimeSample()
    {
        var record = new LandingRecord();
        record.Series.Add(new LandingSeriesPoint
        {
            TimeSeconds = -1,
            WindDirectionDegrees = 90,
            WindSpeedKnots = 5,
        });
        record.Series.Add(new LandingSeriesPoint
        {
            TimeSeconds = 0.02,
            WindDirectionDegrees = 275,
            WindSpeedKnots = 13.6,
        });

        Equal("wind 275°/14 kt", record.WindDisplay, "contact wind display");
    }

    private static void LandingHistoryUsesLazyColumnarDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-history-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new LandingRepository(root);
            var record = new LandingRecord
            {
                Id = "columnar-test",
                TimestampUtc = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc),
                AircraftTitle = "Test aircraft",
                InertialFpm = 123,
                SurfaceFpm = double.NaN,
                WindSpeedKnotsAtContact = 12,
                WindDirectionDegreesAtContact = 240,
                RawControllerSourceIndices = new List<int> { 5 },
            };
            record.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = -0.05,
                InertialFpm = 123,
                GForce = double.NaN,
                RawControllerYAxisPercent = new[] { 42.0 },
                RawControllerYAxisValid = new[] { true },
                RawControllerYAxisAgeSeconds = new[] { 0.0 },
            });
            repository.Save(record);
            using (var file = File.OpenRead(repository.IndexPath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
            {
                var indexJson = reader.ReadToEnd();
                Equal(false, indexJson.Contains("NaN"), "summary index contains no NaN literal");
                Equal(false, indexJson.Contains("INF"), "summary index contains no infinity literal");
            }

            File.Delete(repository.IndexPath);
            var reloaded = new LandingRepository(root);
            var summary = reloaded.LoadAll().Single();
            Equal(true, summary.IsSummaryOnly, "history row is summary-only");
            Equal(0, summary.Series.Count, "summary has no detail series");
            AssertNoStorageSentinel(summary);
            var detail = reloaded.LoadDetail(summary) ?? throw new InvalidOperationException("detail did not load");
            Equal(false, detail.IsSummaryOnly, "selected record is a detail");
            Equal(1, detail.Series.Count, "columnar series point count");
            Equal(123.0, detail.Series[0].InertialFpm, "columnar inertial value");
            Equal(true, double.IsNaN(detail.SurfaceFpm), "summary NaN round-trip");
            Equal(true, double.IsNaN(detail.Series[0].GForce), "column NaN round-trip");
            Equal(5, detail.RawControllerSourceIndices[0], "controller source mapping");
            AssertNoStorageSentinel(detail);

            var detailPath = Directory.EnumerateFiles(root, "*.landing.json.gz").Single();
            using (var file = File.OpenRead(detailPath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
            {
                var json = reader.ReadToEnd();
                Equal(true, json.Contains("\"series\""), "columnar series object is present");
                Equal(false, json.Contains("\"TimeSeconds\""), "per-point property names are absent");
                Equal(false, json.Contains("NaN"), "v7 JSON contains no NaN literal");
                Equal(false, json.Contains("INF"), "v7 JSON contains no infinity literal");
            }

            var legacy = new LandingRecord
            {
                FormatVersion = 6,
                Id = "legacy-test",
                TimestampUtc = new DateTime(2026, 8, 4, 11, 0, 0, DateTimeKind.Utc),
            };
            legacy.Series.Add(new LandingSeriesPoint { TimeSeconds = 0, InertialFpm = 321 });
            reloaded.Save(legacy);
            File.Delete(reloaded.IndexPath);
            var rebuilt = new LandingRepository(root);
            var legacySummary = rebuilt.LoadAll().Single(item => item.Id == legacy.Id);
            var legacyDetail = rebuilt.LoadDetail(legacySummary) ?? throw new InvalidOperationException("legacy detail did not load");
            Equal(321.0, legacyDetail.Series[0].InertialFpm, "legacy v6 detail remains readable");

            var columnsType = typeof(MainWindow).Assembly.GetType("LandingStats.App.Storage.LandingSeriesColumns", true)
                              ?? throw new TypeLoadException("LandingSeriesColumns");
            var columns = Activator.CreateInstance(columnsType, true) ?? throw new InvalidOperationException("columns construction failed");
            columnsType.GetProperty("Time")!.SetValue(columns, new double[1]);
            var rejected = false;
            try
            {
                columnsType.GetMethod("Validate")!.Invoke(columns, new object[] { 0 });
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
            {
                rejected = true;
            }
            Equal(true, rejected, "mismatched column lengths are rejected");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void LandingIndexReconcilesCommittedDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-index-reconcile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new LandingRepository(root);
            var first = new LandingRecord { TimestampUtc = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc) };
            repository.Save(first);
            var staleIndex = File.ReadAllBytes(repository.IndexPath);

            var second = new LandingRecord { TimestampUtc = first.TimestampUtc.AddMinutes(1) };
            repository.Save(second);
            File.WriteAllBytes(repository.IndexPath, staleIndex);

            var recovered = new LandingRepository(root).LoadAll();
            Equal(2, recovered.Count, "reconciled landing count");
            Equal(true, recovered.Any(record => record.Id == second.Id), "detail missing from stale index is recovered");
            Equal(2, new LandingRepository(root).LoadAll().Count, "reconciled index is persisted");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void LandingDeleteRemovesDetailAndIndexEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-delete-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new LandingRepository(root);
            var deleted = new LandingRecord
            {
                Id = "delete-me",
                TimestampUtc = new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc),
            };
            var retained = new LandingRecord
            {
                Id = "keep-me",
                TimestampUtc = deleted.TimestampUtc.AddMinutes(1),
            };
            var deletedPath = repository.Save(deleted);
            repository.Save(retained);
            var deletedSummary = repository.LoadAll().Single(record => record.Id == deleted.Id);

            Equal(true, repository.Delete(deleted.Id), "existing landing delete result");
            Equal(false, File.Exists(deletedPath), "deleted detail file is removed");
            Equal(false, repository.LoadAll().Any(record => record.Id == deleted.Id), "in-memory index entry is removed");
            Equal(false, new LandingRepository(root).LoadAll().Any(record => record.Id == deleted.Id), "persisted index entry is removed");
            Equal(true, new LandingRepository(root).LoadAll().Any(record => record.Id == retained.Id), "unrelated landing remains");
            Null(repository.LoadDetail(deletedSummary), "deleted detail cannot be loaded");
            Equal(false, repository.Delete(deleted.Id), "repeated delete is a no-op");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void LandingFilenamesAreCultureInvariant()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-culture-path-" + Guid.NewGuid().ToString("N"));
        var previousCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var record = new LandingRecord
            {
                TimestampUtc = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc),
            };
            var path = new LandingRepository(root).Save(record);
            Equal(true, Path.GetFileName(path).StartsWith("20260805-010203Z-", StringComparison.Ordinal), "invariant UTC filename");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previousCulture;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void AirportCacheSurvivesTransientReadFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-airport-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "airports.json.gz");
        try
        {
            var repository = new AirportFacilityRepository(path);
            repository.MergeAndSave(new[]
            {
                new AirportFacility { Ident = "LBSF", Region = "BG", LatitudeDegrees = 42.7, LongitudeDegrees = 23.4 },
            });

            var rejected = false;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    repository.MergeAndSave(new[]
                    {
                        new AirportFacility { Ident = "LBPD", Region = "BG", LatitudeDegrees = 42.1, LongitudeDegrees = 24.7 },
                    });
                }
                catch (IOException)
                {
                    rejected = true;
                }
            }

            Equal(true, rejected, "transient cache read failure is propagated");
            var restored = repository.Load();
            Equal(1, restored.Count, "existing airport count after failed merge");
            Equal("LBSF", restored[0].Ident, "existing airport survives failed merge");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void CorruptAirportCacheIsRebuilt()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-corrupt-airport-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "airports.json.gz");
        try
        {
            File.WriteAllText(path, "not a gzip stream", Encoding.UTF8);
            var repository = new AirportFacilityRepository(path);
            Equal(0, repository.Load().Count, "corrupt cache load result");
            Equal(false, File.Exists(path), "corrupt cache moved aside");
            Equal(1, Directory.GetFiles(root, "airports.json.gz.corrupt-*").Length, "quarantined cache count");

            var rebuilt = repository.MergeAndSave(new[]
            {
                new AirportFacility { Ident = "LBSF", Region = "BG", LatitudeDegrees = 42.7, LongitudeDegrees = 23.4 },
            });
            Equal(1, rebuilt.Count, "rebuilt cache count");
            Equal("LBSF", repository.Load()[0].Ident, "rebuilt cache content");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void EpisodeAirportSnapshotKeepsNewerRefresh()
    {
        var staleEpisodeSnapshot = new[]
        {
            new AirportFacility
            {
                Ident = "LBSF",
                Region = "BG",
                LatitudeDegrees = 42.6900,
                LongitudeDegrees = 23.4000,
            },
        };
        var latestRefresh = new[]
        {
            new AirportFacility
            {
                Ident = "LBSF",
                Region = "BG",
                LatitudeDegrees = 42.6952,
                LongitudeDegrees = 23.4062,
            },
            new AirportFacility
            {
                Ident = "UUEE",
                Region = "RU",
                LatitudeDegrees = 55.9726,
                LongitudeDegrees = 37.4146,
            },
        };

        var merged = MainWindow.MergeAirportFacilities(staleEpisodeSnapshot, latestRefresh);
        Equal(2, merged.Count, "latest facility count");
        var lbsf = merged.Single(facility => facility.Ident == "LBSF");
        Near(42.6952, lbsf.LatitudeDegrees, 1e-12, "latest duplicate facility wins");
        Equal(true, merged.Any(facility => facility.Ident == "UUEE"), "new refresh facility survives stale episode completion");
    }

    private static void ChartRangesDiscardNonFiniteTelemetry()
    {
        var finite = ChartValueSanitizer.FiniteValues(new[]
        {
            double.NaN,
            12.5,
            double.PositiveInfinity,
            -3.0,
            double.NegativeInfinity,
        });
        Equal(2, finite.Length, "finite chart value count");
        Near(12.5, finite[0], 0, "first finite chart value");
        Near(-3.0, finite[1], 0, "second finite chart value");
    }

    private static void ChartValueTicksIncludeZero()
    {
        var crossingTicks = LandingChart.ValueTicks(-1300.0, 200.0);
        Equal(true, crossingTicks.Any(value => Math.Abs(value) < 1e-12), "crossing range zero tick");
        Equal(1, crossingTicks.Count(value => Math.Abs(value) < 1e-12), "crossing range unique zero tick");

        var boundaryTicks = LandingChart.ValueTicks(0.0, 50.0);
        Equal(1, boundaryTicks.Count(value => Math.Abs(value) < 1e-12), "boundary range unique zero tick");

        var positiveTicks = LandingChart.ValueTicks(0.8, 1.6);
        Equal(false, positiveTicks.Any(value => Math.Abs(value) < 1e-12), "positive range has no artificial zero");
    }

    private static void LaneHoverFindsNearestPoint()
    {
        var points = new[]
        {
            new LandingSeriesPoint { TimeSeconds = -2 },
            new LandingSeriesPoint { TimeSeconds = 0 },
            new LandingSeriesPoint { TimeSeconds = 5 },
            new LandingSeriesPoint { TimeSeconds = 9 },
        };

        Equal(0, MainWindow.ClosestSeriesPointIndex(points, -10), "hover before series");
        Equal(1, MainWindow.ClosestSeriesPointIndex(points, 1), "hover nearest lower point");
        Equal(2, MainWindow.ClosestSeriesPointIndex(points, 4), "hover nearest upper point");
        Equal(3, MainWindow.ClosestSeriesPointIndex(points, 20), "hover after series");
    }

    private static void A340GearChartGroupsFourStruts()
    {
        var contacts = new List<LandingContactSeries>
        {
            GearContactSeries(0, 1.40, 24.0),
            GearContactSeries(1, 0.30, 20.0),
            GearContactSeries(2, 0.28, 21.0),
            GearContactSeries(3, 0.28, 19.0),
            GearContactSeries(4, 0.98, 38.0),
            GearContactSeries(5, 0.98, 39.0),
            GearContactSeries(6, 1.04, 40.0),
            GearContactSeries(7, 0.00, 31.0),
            GearContactSeries(8, 0.13, 32.0),
            GearContactSeries(10, 2.80, 1.0, endTime: 3.40),
            GearContactSeries(11, 1.67, 14.0),
            GearContactSeries(12, 0.94, 15.0),
            GearContactSeries(13, 1.10, 5.0, endTime: 2.30),
            GearContactSeries(14, 1.90, 11.0),
            GearContactSeries(15, 0.50, 8.0, endTime: 1.80),
        };

        var record = new LandingRecord
        {
            AircraftTitle = "ToLiss A340-600",
            ContactPoints = contacts,
        };
        var gear = LandingGearSeriesBuilder.Build(record);

        Equal(4, gear.Count, "A340 displayed strut count");
        Equal(LandingGearRole.Nose, gear[0].Role, "A340 nose role");
        Equal(LandingGearRole.MainOne, gear[1].Role, "A340 first main role");
        Equal(LandingGearRole.MainTwo, gear[2].Role, "A340 second main role");
        Equal(LandingGearRole.MainThree, gear[3].Role, "A340 third neutral main role");
        Equal("0", string.Join(",", gear[0].ContactPointIndices), "A340 nose members");
        Equal("1,2,3", string.Join(",", gear[1].ContactPointIndices), "A340 first main members");
        Equal("4,5,6", string.Join(",", gear[2].ContactPointIndices), "A340 second main members");
        Equal("7,8", string.Join(",", gear[3].ContactPointIndices), "A340 third main members");
        Near(20.0, gear[1].Points[gear[1].Points.Count - 1].CompressionPercent, 1e-12, "A340 averaged main compression");
        Same(gear, LandingGearSeriesBuilder.Build(record), "A340 grouping cached per immutable record");

        var otherA340 = LandingGearSeriesBuilder.Build(new LandingRecord
        {
            AircraftTitle = "Generic A340-600",
            ContactPoints = contacts,
        });
        Equal(
            false,
            otherA340.Any(series => series.Role != LandingGearRole.Generic),
            "unknown A340 variants do not inherit ToLiss CP semantics");
    }

    private static void A340GearChartSurvivesCrosswindAndTouchAndGo()
    {
        var contacts = new List<LandingContactSeries>
        {
            GearContactSeries(0, 1.50, 18.0, endTime: 2.50),
            GearContactSeries(1, 0.00, 42.0, endTime: 2.50),
            GearContactSeries(2, 0.30, 40.0, endTime: 2.50),
            GearContactSeries(3, 0.60, 41.0, endTime: 2.50),
            GearContactSeries(4, 0.85, 9.0, endTime: 2.50),
            GearContactSeries(5, 1.10, 10.0, endTime: 2.50),
            GearContactSeries(6, 1.35, 8.0, endTime: 2.50),
            GearContactSeries(7, 0.10, 25.0, endTime: 2.50),
            GearContactSeries(8, 0.35, 27.0, endTime: 2.50),
        };
        var record = new LandingRecord
        {
            AircraftTitle = "ToLiss A340-600",
            ContactPoints = contacts,
        };

        var gear = LandingGearSeriesBuilder.Build(record);

        Equal(4, gear.Count, "crosswind A340 strut count after airborne window end");
        Equal("1,2,3", string.Join(",", gear[1].ContactPointIndices), "crosswind first main remains separate");
        Equal("4,5,6", string.Join(",", gear[2].ContactPointIndices), "crosswind second main remains separate");
        Near(41.0, gear[1].PeakCompressionPercent, 1.1, "crosswind loaded main preserved");
        Near(9.0, gear[2].PeakCompressionPercent, 1.1, "crosswind light main preserved");
    }

    private static void A340GearChartExcludesHelpersWithoutNoseContact()
    {
        var contacts = new List<LandingContactSeries>();
        for (var index = 1; index <= 8; index++)
        {
            contacts.Add(GearContactSeries(index, 0.1 * index, 20.0 + index));
        }
        contacts.Add(GearContactSeries(11, 1.2, 12.0));
        contacts.Add(GearContactSeries(12, 1.3, 13.0));
        contacts.Add(GearContactSeries(14, 1.4, 14.0));
        var record = new LandingRecord
        {
            AircraftTitle = "ToLiss A340-600",
            ContactPoints = contacts,
        };

        var gear = LandingGearSeriesBuilder.Build(record);

        Equal(3, gear.Count, "A340 contacted struts without nose contact");
        Equal(false, gear.SelectMany(series => series.ContactPointIndices).Any(index => index > 8), "A340 helpers excluded");
        Equal(LandingGearRole.MainOne, gear[0].Role, "first remaining A340 role");
        Equal(LandingGearRole.MainThree, gear[2].Role, "last remaining A340 role");
    }

    private static void FourthEngineThrottleUsesMatchingColor()
    {
        Same(LandingChart.SeriesBrushAt(3), LandingChart.PowerThrottleBrushAt(3), "engine four N1/throttle color");
    }

    private static LandingContactSeries GearContactSeries(
        int contactPointIndex,
        double startTime,
        double compressionPercent,
        double endTime = double.PositiveInfinity)
    {
        var series = new LandingContactSeries { ContactPointIndex = contactPointIndex };
        for (var index = 0; index <= 60; index++)
        {
            var time = -1.0 + index * 0.10;
            var onGround = time >= startTime - 1e-9 && time <= endTime + 1e-9;
            series.Points.Add(new LandingContactPoint
            {
                TimeSeconds = time,
                OnGround = onGround,
                CompressionPercent = onGround ? compressionPercent : 0.0,
                PositionPercent = onGround ? 100.0 : 0.0,
            });
        }

        return series;
    }

    private static void MonthlyAverageCountsPrimaryContactsOnly()
    {
        var timestamp = DateTime.UtcNow;
        var records = new[]
        {
            new LandingRecord { TimestampUtc = timestamp, ContactNumber = 1, ContactCount = 2, InertialFpm = -100 },
            new LandingRecord { TimestampUtc = timestamp, ContactNumber = 2, ContactCount = 2, InertialFpm = -900 },
            new LandingRecord { TimestampUtc = timestamp, ContactNumber = 1, ContactCount = 1, InertialFpm = -300 },
        };

        var average = MainWindow.MonthlyAverageDisplayedFpm(records, DateTime.Now);
        Equal(true, average.HasValue, "monthly average availability");
        Near(200, average!.Value, 0, "secondary bounce contact excluded");
    }

    private static void StoredControllerColumnsAreCompact()
    {
        var samples = new List<TelemetrySample>();
        for (var index = 0; index < 32; index++)
        {
            var time = -1.55 + index * 0.05;
            var sample = new TelemetrySample
            {
                SimulationTimeSeconds = time,
                OnGround = false,
                AboveGroundLevelFeet = Math.Max(0, -time * 100),
                PilotPitchInputPercent = index * 2,
            };
            sample.RawControllerYAxisValid[5] = true;
            sample.RawControllerYAxisPercent[5] = index * 2;
            samples.Add(sample);
        }

        var result = new TouchdownResult { ContactNumber = 1, EstimatedContactTimeSeconds = 0 };
        var record = LandingRecordFactory.Create(result, samples, "Test", "TEST");
        Equal(1, record.RawControllerSourceIndices.Count, "stored controller column count");
        Equal(5, record.RawControllerSourceIndices[0], "original controller source index");
        Equal(1, record.Series[0].RawControllerYAxisPercent.Length, "per-point controller width");
        Equal(5, record.RawPitchInputSourceIndex, "pitch selector retains original source index");
    }

    private static void ClosureReconstructionSurvivesHistoryRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-reconstruction-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new TouchdownResult
            {
                ContactNumber = 1,
                EstimatedContactTimeSeconds = 0,
                ClosureReconstructionModel = "quad250-tc-minus-75ms-pitch-v1",
                ClosureReconstructionAvailable = true,
                ReconstructedClosureFpm = 300.0,
                ReconstructedInertialFpm = 270.0,
                ReconstructedTerrainFpm = 20.0,
                ReconstructedPitchFpm = 10.0,
                ClosureReconstructionResidualFpm = 7.0,
                ClosureReconstructionUncertaintyFpm = 10.0,
                ClosureReconstructionFitPointCount = 7,
                ClosureReconstructionLongitudinalArmFeet = -7.5,
                ClosureReconstructionGeometryQuality = 0.84,
                ClosureReconstructionArmRecoveredFromTelemetry = true,
                ClosureReconstructionGeometrySource = TouchdownGeometrySource.Telemetry,
            };
            var record = LandingRecordFactory.Create(result, Array.Empty<TelemetrySample>(), "Test", "TEST");

            Equal(true, record.HasClosureReconstruction, "factory reconstruction availability");
            Equal(result.ClosureReconstructionModel, record.ClosureReconstructionModel, "factory model id");
            Near(result.ReconstructedClosureFpm, record.ReconstructedClosureFpm, 1e-12, "factory modeled closure");
            Equal(true, record.ClosureGeometryDisplay.Contains("telemetry"), "telemetry geometry provenance display");

            var repository = new LandingRepository(root);
            repository.Save(record);
            var summary = repository.LoadAll().Single();
            Equal(true, summary.HasClosureReconstruction, "summary reconstruction availability");
            Near(7.0, summary.ClosureReconstructionResidualFpm, 1e-12, "summary residual");
            Near(0.84, summary.ClosureReconstructionGeometryQuality, 1e-12, "summary geometry quality");
            AssertNoStorageSentinel(summary);

            var detail = repository.LoadDetail(summary) ?? throw new InvalidOperationException("reconstruction detail did not load");
            Equal(result.ClosureReconstructionModel, detail.ClosureReconstructionModel, "detail model id");
            Near(300.0, detail.ReconstructedClosureFpm, 1e-12, "detail modeled closure");
            Near(270.0, detail.ReconstructedInertialFpm, 1e-12, "detail inertial component");
            Near(20.0, detail.ReconstructedTerrainFpm, 1e-12, "detail terrain component");
            Near(10.0, detail.ReconstructedPitchFpm, 1e-12, "detail pitch component");
            Near(10.0, detail.ClosureReconstructionUncertaintyFpm, 1e-12, "detail uncertainty");
            Equal(7, detail.ClosureReconstructionFitPointCount, "detail fit point count");
            Near(-7.5, detail.ClosureReconstructionLongitudinalArmFeet, 1e-12, "detail longitudinal arm");
            Equal(true, detail.ClosureReconstructionArmRecoveredFromTelemetry, "detail arm provenance");
            Equal(nameof(TouchdownGeometrySource.Telemetry), detail.ClosureReconstructionGeometrySource, "detail geometry source");
            AssertNoStorageSentinel(detail);

            var legacyPath = Path.Combine(root, "20260804-000000Z-legacy-missing-reconstruction.landing.json.gz");
            using (var file = File.Create(legacyPath))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal, false))
            using (var writer = new StreamWriter(gzip))
            {
                writer.Write("{\"layout\":7,\"summary\":{\"FormatVersion\":7,\"Id\":\"legacy-missing-reconstruction\",\"TimestampUtc\":\"\\/Date(0)\\/\"}}");
            }

            File.Delete(repository.IndexPath);
            var rebuilt = new LandingRepository(root);
            var legacy = rebuilt.LoadAll().Single(item => item.Id == "legacy-missing-reconstruction");
            Equal(false, legacy.HasClosureReconstruction, "legacy reconstruction availability");
            Equal("reconstruction unavailable", legacy.ClosureModelDisplay, "legacy reconstruction display");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void LegacyReconstructionProvenanceRemainsTelemetry()
    {
        var result = new TouchdownResult
        {
            ContactNumber = 1,
            EstimatedContactTimeSeconds = 0,
            ClosureReconstructionAvailable = true,
            ReconstructedClosureFpm = 250.0,
            ClosureReconstructionLongitudinalArmFeet = -7.5,
            ClosureReconstructionArmRecoveredFromTelemetry = true,
            // Older producers do not know ClosureReconstructionGeometrySource,
            // so the new enum remains at its Unavailable default.
        };

        var record = LandingRecordFactory.Create(result, Array.Empty<TelemetrySample>(), "Test", "TEST");
        Equal(
            nameof(TouchdownGeometrySource.Telemetry),
            record.ClosureReconstructionGeometrySource,
            "legacy factory geometry source");
        Equal(true, record.ClosureGeometryDisplay.Contains("telemetry"), "legacy telemetry provenance display");

        record.ClosureReconstructionGeometrySource = nameof(TouchdownGeometrySource.Unavailable);
        Equal(true, record.ClosureGeometryDisplay.Contains("telemetry"), "stored unavailable source uses legacy provenance");
    }

    private static void ClosureReconstructionKeepsRawLatchIndependent()
    {
        const double armFeet = -8.0;
        const double contactTime = 0.010;
        const double evaluationOffset = -0.075;
        var expectedInertial = -Velocity(evaluationOffset) * 60.0;
        var expectedTerrain = GroundDerivative(evaluationOffset) * 60.0;
        var expectedPitch = PitchRate(evaluationOffset) * armFeet *
                            Math.Cos(PitchRadians(evaluationOffset)) *
                            Math.Cos(BankRadians(evaluationOffset)) * 60.0;
        var expectedClosure = expectedInertial + expectedTerrain + expectedPitch;
        var rawLatch = expectedClosure + 7.0;
        var samples = ReconstructionSamples(contactTime, rawLatch);

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(true, result.ContactTimeEstimatedFromCompression, "compression contact time");
        Near(contactTime, result.EstimatedContactTimeSeconds, 1e-9, "estimated contact time");
        Equal(true, result.ClosureReconstructionAvailable, "reconstruction availability");
        Equal("quad250-tc-minus-75ms-pitch-v1", result.ClosureReconstructionModel, "frozen model id");
        Near(expectedInertial, result.ReconstructedInertialFpm, 1e-8, "reconstructed inertial component");
        Near(expectedTerrain, result.ReconstructedTerrainFpm, 1e-8, "reconstructed terrain component");
        Near(expectedPitch, result.ReconstructedPitchFpm, 1e-8, "reconstructed pitch component");
        Near(expectedClosure, result.ReconstructedClosureFpm, 1e-8, "reconstructed closure");
        Near(rawLatch, result.LatchedNormalFpm, 1e-8, "raw simulator latch");
        Near(7.0, result.ClosureReconstructionResidualFpm, 1e-8, "model residual");
        Near(10.0, result.ClosureReconstructionUncertaintyFpm, 1e-8, "primary uncertainty");
        Near(armFeet, result.ClosureReconstructionLongitudinalArmFeet, 1e-8, "passport arm");
        Equal(true, double.IsNaN(result.ClosureReconstructionGeometryQuality), "passport geometry quality");
        Equal(false, result.ClosureReconstructionArmRecoveredFromTelemetry, "passport arm provenance");
        Equal(TouchdownGeometrySource.Provided, result.ClosureReconstructionGeometrySource, "passport geometry source");

        var changedLatch = TouchdownAnalysis.Analyze(
            ReconstructionSamples(contactTime, rawLatch + 100.0),
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();
        Near(result.ReconstructedClosureFpm, changedLatch.ReconstructedClosureFpm, 1e-10, "target-independent model");
        Near(rawLatch + 100.0, changedLatch.LatchedNormalFpm, 1e-8, "changed raw latch");

        var nonzeroOmegaY = ReconstructionSamples(contactTime, rawLatch);
        foreach (var sample in nonzeroOmegaY)
        {
            sample.RotationVelocityBodyYRadiansPerSecond = 0.75;
        }

        var frozenWithoutYawBank = TouchdownAnalysis.Analyze(
            nonzeroOmegaY,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();
        Near(
            result.ReconstructedClosureFpm,
            frozenWithoutYawBank.ReconstructedClosureFpm,
            1e-10,
            "v1 deliberately omits omega-Y yaw-bank term");
    }

    private static void ClosureReconstructionRequiresGeometry()
    {
        var samples = ReconstructionSamples(0.010, 280.0);
        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ClosureReconstructionAvailable, "reconstruction without an arm");
        Equal(true, double.IsNaN(result.ReconstructedClosureFpm), "unavailable modeled closure");
        Near(280.0, result.LatchedNormalFpm, 1e-8, "raw latch remains available");
    }

    private static void ClosureReconstructionRequiresFiveFitPoints()
    {
        const double armFeet = -8.0;
        var options = new TouchdownAnalysisOptions
        {
            LongitudinalMainGearArmFeet = armFeet,
            RecoverLongitudinalMainGearArmFromTelemetry = false,
        };

        var fourPoint = TouchdownAnalysis.Analyze(
            SparseReconstructionSamples(4),
            options).Single();
        Equal(false, fourPoint.ClosureReconstructionAvailable, "four-point reconstruction availability");
        Equal(0, fourPoint.ClosureReconstructionFitPointCount, "four-point fit count");

        var fivePoint = TouchdownAnalysis.Analyze(
            SparseReconstructionSamples(5),
            options).Single();
        Equal(true, fivePoint.ClosureReconstructionAvailable, "five-point reconstruction availability");
        Equal(5, fivePoint.ClosureReconstructionFitPointCount, "five-point fit count");
    }

    private static void ClosureReconstructionRequiresDistinctTimestamps()
    {
        var samples = SparseReconstructionSamples(3);
        var airborne = samples.Where(sample => !sample.OnGround).ToArray();
        samples.Insert(1, CloneTelemetrySample(airborne[0]));
        samples.Insert(3, CloneTelemetrySample(airborne[1]));
        samples.Insert(5, CloneTelemetrySample(airborne[2]));

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ClosureReconstructionAvailable, "duplicate timestamps cannot satisfy fit minimum");
    }

    private static void ClosureReconstructionRequiresEvaluationBracket()
    {
        var samples = DenseShortHistoryReconstructionSamples();
        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ClosureReconstructionAvailable, "tc-75 ms outside sampled history");
    }

    private static void ClosureReconstructionSanitizesEarlyGroundSpike()
    {
        const double armFeet = -8.0;
        var baselineSamples = ReconstructionSamples(0.010, 280.0).Skip(4).ToList();
        var baseline = TouchdownAnalysis.Analyze(
            baselineSamples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        var spikedSamples = ReconstructionSamples(0.010, 280.0).Skip(4).ToList();
        spikedSamples[1].GroundAltitudeFeet += 10.0;
        var repaired = TouchdownAnalysis.Analyze(
            spikedSamples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(true, repaired.ClosureReconstructionAvailable, "early-spike reconstruction availability");
        Near(baseline.ReconstructedTerrainFpm, repaired.ReconstructedTerrainFpm, 0.05, "early ground spike repair");
    }

    private static void ClosureReconstructionMarksContactTimeFallback()
    {
        const double armFeet = -8.0;
        var samples = ReconstructionSamples(0.010, 280.0);
        foreach (var sample in samples)
        {
            Array.Clear(sample.ContactPointCompression, 0, sample.ContactPointCompression.Length);
        }

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ContactTimeEstimatedFromCompression, "last-air contact-time provenance");
        Equal(true, result.ClosureReconstructionAvailable, "fallback reconstruction availability");
        Near(15.0, result.ClosureReconstructionUncertaintyFpm, 1e-8, "fallback uncertainty");
    }

    private static void ClosureReconstructionAcceptsPermutedStaggeredMains()
    {
        const double armFeet = -8.0;
        var samples = ReconstructionSamples(
            0.010,
            280.0,
            firstMainIndex: 17,
            secondMainIndex: 4,
            noseIndex: 11,
            secondMainDelaySeconds: 2.50,
            noseContactTime: 4.80,
            sampleEndTime: 5.20);

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(true, result.ClosureReconstructionAvailable, "permuted staggered-main reconstruction");
        Near(armFeet, result.ClosureReconstructionLongitudinalArmFeet, 1e-12, "permuted topology arm");
    }

    private static void ClosureReconstructionAcceptsCenterMainConservatively()
    {
        var samples = ReconstructionSamples(0.010, 280.0);
        AddSettledMainPoint(samples, 7);

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(true, result.ClosureReconstructionAvailable, "four-point reconstruction availability");
        Near(15.0, result.ClosureReconstructionUncertaintyFpm, 1e-12, "center-main uncertainty");
    }

    private static void ClosureReconstructionAcceptsClusteredA340Wheels()
    {
        var samples = ReconstructionSamples(
            0.010,
            280.0,
            noseContactTime: 0.60,
            sampleEndTime: 15.00);
        AddSettledPoint(samples, 3, 0.050);
        AddSettledPoint(samples, 7, 0.075);
        AddSettledPoint(samples, 8, 0.100);
        AddSettledPoint(samples, 4, 0.625);
        AddSettledPoint(samples, 5, 0.650);
        AddSettledPoint(samples, 6, 0.675);
        AddSettledPoint(samples, 11, 0.700);
        AddSettledPoint(samples, 12, 0.725);
        AddSettledPoint(samples, 14, 0.750);
        AddTemporaryPoint(samples, 15, 0.050, 0.900);
        AddTemporaryPoint(samples, 13, 0.650, 0.900);
        // Reproduce a short automatic capture ending while a rollout-only helper
        // contact is active. The truncated run must not become a thirteenth
        // "settled wheel" and invalidate the genuine A340 gear clusters.
        AddTemporaryPoint(samples, 10, 13.000, 20.000);

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                LongitudinalMainGearArmSource = TouchdownGeometrySource.Telemetry,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(true, result.ClosureReconstructionAvailable, "clustered-wheel reconstruction availability");
        Near(50.0, result.ClosureReconstructionUncertaintyFpm, 1e-12, "clustered-wheel uncertainty");

        var configured = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                LongitudinalMainGearArmSource = TouchdownGeometrySource.FlightModelConfig,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();
        Near(15.0, configured.ClosureReconstructionUncertaintyFpm, 1e-12, "configured multi-bogie uncertainty");
    }

    private static void ClosureReconstructionAcceptsIrregularA340WheelTiming()
    {
        var samples = ReconstructionSamples(
            0.010,
            377.0,
            firstMainIndex: 7,
            secondMainIndex: 8,
            secondMainDelaySeconds: 0.125,
            noseIndex: 0,
            noseContactTime: 1.425,
            sampleEndTime: 15.00);
        AddSettledPoint(samples, 2, 0.275);
        AddSettledPoint(samples, 3, 0.275);
        AddSettledPoint(samples, 1, 0.300);
        AddSettledPoint(samples, 12, 0.925);
        AddSettledPoint(samples, 4, 0.975);
        AddSettledPoint(samples, 5, 0.975);
        AddSettledPoint(samples, 6, 1.025);
        AddSettledPoint(samples, 11, 1.675);
        AddSettledPoint(samples, 14, 1.925);
        AddTemporaryPoint(samples, 15, 0.525, 1.800);
        AddTemporaryPoint(samples, 13, 1.125, 2.350);
        AddTemporaryPoint(samples, 10, 8.625, 9.450);

        // The latest real A340 trace gained only about 0.45 degrees between
        // the final main-wheel onset and the first later cluster. That is
        // still coherent positive derotation, but the previous 0.50-degree
        // clustered-wheel gate rejected the otherwise complete topology.
        foreach (var sample in samples)
        {
            var time = sample.SimulationTimeSeconds;
            sample.PitchDegrees = time <= 0.300
                ? -3.400
                : time >= 0.925
                    ? -2.950
                    : -3.400 + 0.450 * (time - 0.300) / 0.625;
        }

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -10.0,
                LongitudinalMainGearArmSource = TouchdownGeometrySource.Telemetry,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(true, result.ClosureReconstructionAvailable, "irregular A340 reconstruction availability");
        Near(50.0, result.ClosureReconstructionUncertaintyFpm, 1e-12, "irregular A340 uncertainty");

        var configWithNonMatchingCompressionIndices = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                LongitudinalMainGearArmSource = TouchdownGeometrySource.FlightModelConfig,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
                MainGearContactPoints = new[]
                {
                    new TouchdownMainGearContactPoint(1, -9.0),
                    new TouchdownMainGearContactPoint(2, -9.0),
                    new TouchdownMainGearContactPoint(3, -6.0),
                },
                NoseGearContactPointIndices = new[] { 0 },
            }).Single();
        Equal(true, configWithNonMatchingCompressionIndices.ClosureReconstructionAvailable, "A340 CFG-index fallback availability");
        Equal(TouchdownGeometrySource.FlightModelConfig, configWithNonMatchingCompressionIndices.ClosureReconstructionGeometrySource, "A340 CFG-index fallback source");
        Near(-8.0, configWithNonMatchingCompressionIndices.ClosureReconstructionLongitudinalArmFeet, 1e-12, "A340 CFG-index fallback median arm");
        Near(15.0, configWithNonMatchingCompressionIndices.ClosureReconstructionUncertaintyFpm, 1e-12, "A340 configured-arm fallback uncertainty");
    }

    private static void ClosureReconstructionAcceptsFivePointTopology()
    {
        var samples = ReconstructionSamples(0.010, 280.0);
        AddSettledMainPoint(samples, 7);
        AddSettledMainPoint(samples, 8);

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(true, result.ClosureReconstructionAvailable, "five-point reconstruction availability");
        Near(15.0, result.ClosureReconstructionUncertaintyFpm, 1e-12, "five-point uncertainty");
        Near(280.0, result.LatchedNormalFpm, 1e-8, "five-point raw latch fallback");
    }

    private static void ConfiguredGearTopologyIgnoresHelpers()
    {
        var samples = ReconstructionSamples(0.010, 280.0);
        AddSettledMainPoint(samples, 7);
        AddSettledMainPoint(samples, 8);
        AddSettledMainPoint(samples, 11);

        var withoutConfiguration = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -3.5,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();
        Equal(false, withoutConfiguration.ClosureReconstructionAvailable, "ambiguous helper topology without configuration");

        var configured = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -3.5,
                LongitudinalMainGearArmSource = TouchdownGeometrySource.FlightModelConfig,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
                MainGearContactPoints = new[]
                {
                    new TouchdownMainGearContactPoint(1, -11.0),
                    new TouchdownMainGearContactPoint(2, -11.0),
                    new TouchdownMainGearContactPoint(7, 4.0),
                    new TouchdownMainGearContactPoint(8, 4.0),
                },
                NoseGearContactPointIndices = new[] { 0 },
            }).Single();

        Equal(true, configured.ClosureReconstructionAvailable, "configured helper topology reconstruction");
        Equal(TouchdownGeometrySource.FlightModelConfig, configured.ClosureReconstructionGeometrySource, "configured helper topology source");
        Near(-3.5, configured.ClosureReconstructionLongitudinalArmFeet, 1e-12, "contact-active configured arm");
    }

    private static void AddSettledMainPoint(IReadOnlyList<TelemetrySample> samples, int point)
    {
        foreach (var sample in samples)
        {
            if (!sample.OnGround)
            {
                continue;
            }

            sample.ContactPointOnGround[point] = true;
            sample.ContactPointCompression[point] = (sample.SimulationTimeSeconds - 0.010) * 100.0;
        }
    }

    private static void AddSettledPoint(
        IReadOnlyList<TelemetrySample> samples,
        int point,
        double startTime)
    {
        foreach (var sample in samples)
        {
            if (sample.SimulationTimeSeconds < startTime - 1e-9)
            {
                continue;
            }

            sample.ContactPointOnGround[point] = true;
            sample.ContactPointCompression[point] =
                Math.Max(0.01, (sample.SimulationTimeSeconds - startTime) * 100.0);
        }
    }

    private static void AddTemporaryPoint(
        IReadOnlyList<TelemetrySample> samples,
        int point,
        double startTime,
        double endTime)
    {
        foreach (var sample in samples)
        {
            if (sample.SimulationTimeSeconds < startTime - 1e-9 ||
                sample.SimulationTimeSeconds > endTime + 1e-9)
            {
                continue;
            }

            sample.ContactPointOnGround[point] = true;
            sample.ContactPointCompression[point] =
                Math.Max(0.01, (sample.SimulationTimeSeconds - startTime) * 100.0);
        }
    }

    private static void ClosureReconstructionKeepsMainGearBounces()
    {
        const double armFeet = -8.0;
        var samples = ReconstructionSamples(0.010, 280.0, noseContactTime: 0.575);
        foreach (var sample in samples)
        {
            var time = sample.SimulationTimeSeconds;
            var firstMainContact = time >= 0.025 - 1e-9 && time <= 0.100 + 1e-9;
            var settledMainContact = time >= 0.275 - 1e-9;
            var mainContact = firstMainContact || settledMainContact;
            sample.OnGround = mainContact;
            sample.TouchdownNormalVelocityFps = mainContact ? 280.0 / 60.0 : 0.0;
            Array.Clear(sample.ContactPointOnGround, 0, sample.ContactPointOnGround.Length);
            Array.Clear(sample.ContactPointCompression, 0, sample.ContactPointCompression.Length);
            if (mainContact)
            {
                sample.ContactPointOnGround[1] = true;
                sample.ContactPointOnGround[2] = true;
                var crossing = firstMainContact ? 0.010 : 0.260;
                sample.ContactPointCompression[1] = (time - crossing) * 100.0;
                sample.ContactPointCompression[2] = (time - crossing) * 100.0;
            }

            if (time >= 0.575 - 1e-9)
            {
                sample.ContactPointOnGround[0] = true;
            }
        }

        var results = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            });

        Equal(2, results.Count, "main-bounce contact count");
        Equal(true, results[0].ClosureReconstructionAvailable, "first main contact reconstruction");
        Equal(true, results[1].ClosureReconstructionAvailable, "main recontact reconstruction");
        Near(15.0, results[0].ClosureReconstructionUncertaintyFpm, 1e-12, "pre-bounce uncertainty");
        Near(15.0, results[1].ClosureReconstructionUncertaintyFpm, 1e-12, "recontact uncertainty");
    }

    private static void ClosureReconstructionRejectsNoseFirstContact()
    {
        const double rawLatchFpm = 280.0;
        var samples = ReconstructionSamples(0.010, rawLatchFpm);
        foreach (var sample in samples)
        {
            var time = sample.SimulationTimeSeconds;
            Array.Clear(sample.ContactPointOnGround, 0, sample.ContactPointOnGround.Length);
            Array.Clear(sample.ContactPointCompression, 0, sample.ContactPointCompression.Length);
            if (!sample.OnGround)
            {
                continue;
            }

            sample.ContactPointOnGround[11] = true;
            sample.ContactPointCompression[11] = (time - 0.010) * 100.0;
            if (time >= 0.350 - 1e-9)
            {
                sample.ContactPointOnGround[4] = true;
                sample.ContactPointOnGround[17] = true;
                sample.ContactPointCompression[4] = (time - 0.335) * 100.0;
                sample.ContactPointCompression[17] = (time - 0.335) * 100.0;
            }
        }

        var legacy = TouchdownAnalysis.Analyze(samples).Single();
        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ClosureReconstructionAvailable, "nose-first reconstruction availability");
        Near(rawLatchFpm, result.LatchedNormalFpm, 1e-8, "nose-first raw latch");
        Near(legacy.LatchedNormalFpm, result.LatchedNormalFpm, 1e-12, "raw latch independent of topology gate");
        Near(legacy.InertialVerticalFpm, result.InertialVerticalFpm, 1e-12, "inertial metric independent of topology gate");
        Near(legacy.PeakG, result.PeakG, 1e-12, "peak G independent of topology gate");
    }

    private static void ClosureReconstructionRejectsMixedMainNoseContact()
    {
        const double rawLatchFpm = 280.0;
        var samples = ReconstructionSamples(0.010, rawLatchFpm);
        foreach (var sample in samples)
        {
            var time = sample.SimulationTimeSeconds;
            if (time >= 0.050 - 1e-9 && time <= 0.100 + 1e-9)
            {
                sample.ContactPointOnGround[0] = true;
                sample.ContactPointCompression[0] = (time - 0.035) * 100.0;
            }
        }

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ClosureReconstructionAvailable, "mixed main-nose reconstruction availability");
        Near(rawLatchFpm, result.LatchedNormalFpm, 1e-8, "mixed main-nose raw latch fallback");
    }

    private static void TelemetryGeometryRecoversAnalyticArm()
    {
        const double pitchArmFeet = -12.0;
        const double datumOffsetFeet = 4.0;
        const double expectedArmFeet = pitchArmFeet + datumOffsetFeet;
        const double groundAltitudeFeet = 100.0;
        const double baseHeightFeet = 14.0;
        const double compressionCoefficient = -0.02;
        const double step = 0.025;
        const int firstMainIndex = 17;
        const int secondMainIndex = 4;
        const int noseIndex = 11;
        var samples = new List<TelemetrySample>();
        var integratedPlane = 0.0;
        var integratedPlaneAtContact = double.NaN;
        var previousVertical = 0.0;

        for (var index = 0; index <= 200; index++)
        {
            var time = -4.0 + index * step;
            var onGround = time >= -1e-9;
            var pitch = onGround ? 0.05 + 0.02 * Math.Sin(5.0 * time) + 0.05 * time : 0.0;
            var compression = onGround ? 20.0 + 5.0 * Math.Cos(7.0 * time) : 0.0;
            var omegaX = 0.01 + 0.004 * Math.Sin(1.7 * time) + 0.001 * time;
            var worldY = -4.0 + 0.15 * Math.Sin(0.9 * time);
            var rigidVerticalPerFoot = -omegaX;
            var verticalAtDatum = worldY + datumOffsetFeet * rigidVerticalPerFoot;
            if (index > 0)
            {
                integratedPlane += 0.5 * step * (previousVertical + verticalAtDatum);
            }

            previousVertical = verticalAtDatum;
            var sample = new TelemetrySample
            {
                Sequence = index,
                SimulationTimeSeconds = time,
                OnGround = onGround,
                VelocityWorldYFps = worldY,
                RotationVelocityBodyXRadiansPerSecond = omegaX,
                PitchDegrees = pitch * 180.0 / Math.PI,
                GroundAltitudeFeet = groundAltitudeFeet,
                PlaneAltitudeFeet = integratedPlane,
            };
            if (onGround)
            {
                if (double.IsNaN(integratedPlaneAtContact))
                {
                    integratedPlaneAtContact = integratedPlane;
                }

                sample.ContactPointOnGround[noseIndex] = time >= 0.60;
                sample.ContactPointOnGround[firstMainIndex] = true;
                sample.ContactPointOnGround[secondMainIndex] = true;
                sample.ContactPointCompression[firstMainIndex] = compression;
                sample.ContactPointCompression[secondMainIndex] = compression;
                sample.PlaneAltitudeFeet = groundAltitudeFeet + baseHeightFeet +
                                           pitchArmFeet * pitch +
                                           compressionCoefficient * compression;
            }

            samples.Add(sample);
        }

        var contactIndex = samples.FindIndex(sample => sample.OnGround);
        var contactPlane = samples[contactIndex].PlaneAltitudeFeet;
        var airborneShift = contactPlane - integratedPlaneAtContact;
        for (var index = 0; index < contactIndex; index++)
        {
            samples[index].PlaneAltitudeFeet += airborneShift;
        }

        Equal(true, TelemetryGeometryCalibration.TryCalibrate(samples, out var calibration), "geometry calibration");
        Equal(true, TelemetryGeometryCalibration.TryRecoverDatumOffset(samples, out var datumOnly), "datum-only calibration");
        Near(pitchArmFeet, calibration.PitchArmFeet, 1e-8, "pitch arm");
        Near(datumOffsetFeet, calibration.DatumOffsetFeet, 1e-8, "datum offset");
        Near(datumOffsetFeet, datumOnly.DatumOffsetFeet, 1e-8, "datum-only offset");
        Equal(4, datumOnly.PhaseCount, "datum-only phase count");
        Near(expectedArmFeet, calibration.LongitudinalArmFeet, 1e-8, "longitudinal arm");
        Equal(4, calibration.DatumPhaseCount, "datum phase count");
        Equal(true, calibration.Quality >= 0.20 && calibration.Quality <= 1.0, "geometry quality range");

        var configRoot = Path.Combine(Path.GetTempPath(), "landing-stats-flight-model-datum-" + Guid.NewGuid().ToString("N"));
        try
        {
            var aircraftRoot = Path.Combine(configRoot, "Community", "analytic-aircraft", "SimObjects", "Airplanes", "AnalyticJet");
            Directory.CreateDirectory(aircraftRoot);
            File.WriteAllText(
                Path.Combine(aircraftRoot, "aircraft.cfg"),
                "[FLTSIM.0]\ntitle = \"AnalyticJet\"\natc_model = \"ANLT\"\nisUserSelectable = 1\n");
            File.WriteAllText(
                Path.Combine(aircraftRoot, "flight_model.cfg"),
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 25, 0, -9\n" +
                "point.1 = 1, -12, -10, -9\n" +
                "point.2 = 1, -12, 10, -9\n");

            var resolver = new FlightModelGeometryResolver(new[] { configRoot }, false);
            Equal(
                true,
                resolver.TryCreateAnalysisOptions("AnalyticJet", "AIRBUS", "ANLT", samples, out var configOptions),
                "flight-model geometry plus telemetry datum");
            Near(expectedArmFeet, configOptions.LongitudinalMainGearArmFeet!.Value, 1e-8, "flight-model corrected arm");
            Equal(TouchdownGeometrySource.FlightModelConfig, configOptions.LongitudinalMainGearArmSource, "flight-model geometry source");
            Near(datumOnly.Quality, configOptions.LongitudinalMainGearArmQuality!.Value, 1e-12, "flight-model datum quality");
            Equal("1,2", string.Join(",", configOptions.MainGearContactPoints.Select(point => point.ContactPointIndex)), "flight-model main indices");
            Equal("0", string.Join(",", configOptions.NoseGearContactPointIndices), "flight-model nose indices");
            Near(-12.0 + datumOnly.DatumOffsetFeet, configOptions.MainGearContactPoints[0].LongitudinalArmFeet, 1e-8, "flight-model point arm");
        }
        finally
        {
            if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        }

        var clusteredMainPoints = new[] { (Point: 1, Start: 0.050), (Point: 2, Start: 0.075), (Point: 3, Start: 0.100) };
        var clusteredNosePoints = new[]
        {
            (Point: 0, Start: 0.625),
            (Point: 5, Start: 0.650),
            (Point: 6, Start: 0.675),
            (Point: 12, Start: 0.700),
            (Point: 14, Start: 0.725),
            (Point: 15, Start: 0.750),
        };
        foreach (var sample in samples.Where(sample => sample.OnGround))
        {
            foreach (var point in clusteredMainPoints)
            {
                if (sample.SimulationTimeSeconds >= point.Start - 1e-9)
                {
                    sample.ContactPointOnGround[point.Point] = true;
                    sample.ContactPointCompression[point.Point] = sample.ContactPointCompression[firstMainIndex];
                }
            }

            foreach (var point in clusteredNosePoints)
            {
                if (sample.SimulationTimeSeconds >= point.Start - 1e-9)
                {
                    sample.ContactPointOnGround[point.Point] = true;
                }
            }
        }

        Equal(
            true,
            TelemetryGeometryCalibration.TryCalibrate(samples, out var clusteredCalibration),
            "clustered-wheel geometry calibration");
        Near(pitchArmFeet, clusteredCalibration.PitchArmFeet, 1e-8, "clustered-wheel pitch arm");
        Near(datumOffsetFeet, clusteredCalibration.DatumOffsetFeet, 1e-8, "clustered-wheel datum offset");
        Near(expectedArmFeet, clusteredCalibration.LongitudinalArmFeet, 1e-8, "clustered-wheel longitudinal arm");
    }

    private static void DatumCalibrationRejectsInconsistentPhases()
    {
        Equal(
            false,
            TelemetryGeometryCalibration.TryScoreDatumCalibration(3, 5.0, out var poorQuality),
            "inconsistent datum phases accepted");
        Equal(true, poorQuality < 0.20, "inconsistent datum quality is not below the acceptance floor");

        Equal(
            true,
            TelemetryGeometryCalibration.TryScoreDatumCalibration(4, 0.5, out var goodQuality),
            "consistent datum phases rejected");
        Equal(true, goodQuality >= 0.20, "consistent datum quality is below the acceptance floor");
    }

    private static void FlightModelParserMergesModularGeometry()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-flight-model-parser-" + Guid.NewGuid().ToString("N"));
        try
        {
            var common = Path.Combine(root, "common.cfg");
            var preset = Path.Combine(root, "preset.cfg");
            Directory.CreateDirectory(root);
            File.WriteAllText(
                common,
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 30, 0, -10\n" +
                "point.1 = 1, -10, -12, -10\n" +
                "point.2 = 1, -10, 12, -10\n" +
                "point.3 = 2, -50, 0, 3\n");
            File.WriteAllText(
                preset,
                "[CONTACT_POINTS]\n" +
                "point.1 = Name:\"Left_main\"#Properties:1, -12.5, -13, -11\n" +
                "point.2 = Name:\"Right_main\"#Properties:1, -12.5, 13, -11\n");

            Equal(
                true,
                FlightModelConfigParser.TryReadMainGearLongitudinal(
                    new[] { common, preset },
                    out var arm,
                    out var sourcePath),
                "modular flight-model parse");
            // Indexed point.N parameters append during modular auto-merge. The
            // two preset mains therefore coexist with the two common mains.
            Near(-11.25, arm, 1e-12, "preset main-gear append and reindex");
            Equal(preset, sourcePath, "geometry source config");
            Equal(
                true,
                FlightModelConfigParser.TryReadGearGeometry(new[] { common, preset }, out var modularGeometry),
                "modular gear topology parse");
            Equal("1,2,4,5", string.Join(",", modularGeometry.MainGearPoints.Select(point => point.ContactPointIndex)), "modular main indices");
            Equal("0", string.Join(",", modularGeometry.NoseGearContactPointIndices), "modular nose indices");

            var a380 = Path.Combine(root, "a380.cfg");
            File.WriteAllText(
                a380,
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 99.15, 0, -15.08\n" +
                "point.1 = 1, -5.7, -11.5, -15.75\n" +
                "point.2 = 1, -5.7, 11.5, -15.75\n" +
                "point.3 = 1, 5.7, -23.0, -15.63\n" +
                "point.4 = 1, 5.7, 23.0, -15.63\n" +
                "point.5 = 2, 4, -84, -3\n");
            Equal(
                true,
                FlightModelConfigParser.TryReadGearGeometry(new[] { a380 }, out var a380Geometry),
                "A380 gear topology parse");
            Near(0.0, a380Geometry.MainGearLongitudinalFeet, 1e-12, "A380 median main arm");
            Equal("1,2,3,4", string.Join(",", a380Geometry.MainGearPoints.Select(point => point.ContactPointIndex)), "A380 main indices");
            Equal("0", string.Join(",", a380Geometry.NoseGearContactPointIndices), "A380 nose index");

            var manual = Path.Combine(root, "manual.cfg");
            File.WriteAllText(
                manual,
                "[MODULAR_MERGE]\nauto = false\n" +
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 25, 0, -9\n" +
                "point.1 = 1, -8, -8, -9\n" +
                "point.2 = 1, -8, 8, -9\n");
            Equal(
                false,
                FlightModelConfigParser.TryReadMainGearLongitudinal(
                    new[] { manual },
                    out _,
                    out _),
                "manual modular merge fails closed");

            var dynamic = Path.Combine(root, "dynamic.cfg");
            File.WriteAllText(
                dynamic,
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 25, 0, -9\n" +
                "point.1 = Name:left[uid]#Properties:1, [left_wheel_position], 2000\n" +
                "point.2 = 1, -8, 8, -9\n");
            Equal(
                false,
                FlightModelConfigParser.TryReadMainGearLongitudinal(
                    new[] { dynamic },
                    out _,
                    out _),
                "unresolved dynamic contact geometry fails closed");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void FlightModelResolverMapsAircraftIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-flight-model-resolver-" + Guid.NewGuid().ToString("N"));
        try
        {
            var packageRoot = Path.Combine(root, "Community", "test-aircraft");
            var simObjectRoot = Path.Combine(packageRoot, "SimObjects", "Airplanes", "TestJet");
            var presetConfig = Path.Combine(simObjectRoot, "presets", "vendor", "TestJet_Default", "config");
            var attachmentConfig = Path.Combine(simObjectRoot, "attachments", "vendor", "Function_Exterior", "config");
            var baseAttachmentConfig = Path.Combine(simObjectRoot, "attachments", "vendor", "BaseGear", "config");
            Directory.CreateDirectory(presetConfig);
            Directory.CreateDirectory(attachmentConfig);
            Directory.CreateDirectory(baseAttachmentConfig);
            File.WriteAllText(
                Path.Combine(presetConfig, "aircraft.cfg"),
                "[GENERAL]\n" +
                "title = \"TestJet Default\"\n" +
                "atc_model = \"TJET\"\n");
            File.WriteAllText(
                Path.Combine(presetConfig, "attached_objects.cfg"),
                "[sim_attachment.0]\n" +
                "alias = \"Function_Exterior\"\n" +
                "attachment_root = \"SimObjects/Airplanes/TestJet/attachments/vendor/Function_Exterior\"\n");
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(attachmentConfig)!, "attachment.cfg"),
                "[Inherit]\n" +
                "base = \"SimObjects/Airplanes/TestJet/attachments/vendor/BaseGear\"\n");
            File.WriteAllText(
                Path.Combine(baseAttachmentConfig, "flight_model.cfg"),
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 25, 0, -9\n" +
                "point.1 = 1, -10, -10, -9\n" +
                "point.2 = 1, -10, 10, -9\n");
            File.WriteAllText(
                Path.Combine(attachmentConfig, "flight_model.cfg"),
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, -18, -10, -9\n" +
                "point.1 = 1, -18, 10, -9\n");

            var guidRoot = Path.Combine(packageRoot, "SimObjects", "Airplanes", "GuidJet");
            var guidCommon = Path.Combine(guidRoot, "common", "config");
            var guidPreset = Path.Combine(guidRoot, "presets", "vendor", "default", "config");
            Directory.CreateDirectory(guidCommon);
            Directory.CreateDirectory(guidPreset);
            File.WriteAllText(
                Path.Combine(guidCommon, "flight_model.cfg"),
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 20, 0, -8\n" +
                "point.1 = 1, -8, -8, -8\n" +
                "point.2 = 1, -8, 8, -8\n");
            File.WriteAllText(
                Path.Combine(guidPreset, "aircraft.cfg"),
                "[GENERAL]\ntitle = \"GuidJet\"\n");
            File.WriteAllText(
                Path.Combine(guidPreset, "attached_objects.cfg"),
                "[SIM_ATTACHMENT.0]\n" +
                "alias = \"UnknownGear\"\n" +
                "attachment_guid = \"{11111111-2222-3333-4444-555555555555}\"\n");

            var baseJet = Path.Combine(root, "Community", "base-package", "SimObjects", "Airplanes", "BaseJet");
            var livery = Path.Combine(root, "Community", "livery-package", "SimObjects", "Airplanes", "BaseJet_Livery");
            Directory.CreateDirectory(baseJet);
            Directory.CreateDirectory(livery);
            File.WriteAllText(
                Path.Combine(root, "Community", "base-package", "manifest.json"),
                "{\"content_type\":\"AIRCRAFT\"}");
            File.WriteAllText(
                Path.Combine(root, "Community", "livery-package", "manifest.json"),
                "{\"content_type\":\"Livery\"}");
            File.WriteAllText(
                Path.Combine(baseJet, "aircraft.cfg"),
                "[FLTSIM.0]\ntitle = \"BaseJet\"\nisUserSelectable = 1\n");
            File.WriteAllText(
                Path.Combine(baseJet, "flight_model.cfg"),
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 20, 0, -8\n" +
                "point.1 = 1, -9, -8, -8\n" +
                "point.2 = 1, -9, 8, -8\n");
            File.WriteAllText(
                Path.Combine(livery, "aircraft.cfg"),
                "[VARIATION]\nbase_container = \"..\\BaseJet\"\n" +
                "[FLTSIM.0]\ntitle = \"BaseJet Livery\"\nisUserSelectable = 1\n");

            var mutableJet = Path.Combine(
                root,
                "Community",
                "mutable-package",
                "SimObjects",
                "Airplanes",
                "MutableJet");
            Directory.CreateDirectory(mutableJet);
            File.WriteAllText(
                Path.Combine(mutableJet, "aircraft.cfg"),
                "[FLTSIM.0]\ntitle = \"MutableJet\"\nisUserSelectable = 1\n");
            var mutableFlightModel = Path.Combine(mutableJet, "flight_model.cfg");
            File.WriteAllText(
                mutableFlightModel,
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 20, 0, -8\n" +
                "point.1 = 1, -5, -8, -8\n" +
                "point.2 = 1, -5, 8, -8\n");
            var mutableResolver = new FlightModelGeometryResolver(new[] { root }, false);
            File.WriteAllText(
                mutableFlightModel,
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 20, 0, -8\n" +
                "point.1 = 1, -7, -8, -8\n" +
                "point.2 = 1, -7, 8, -8\n");
            Equal(
                true,
                mutableResolver.TryResolve("MutableJet", "unknown", "", out var refreshedMutable),
                "exact title refreshes a stale catalog");
            Near(-7, refreshedMutable.MainGearLongitudinalFeet, 1e-12, "refreshed exact-title arm");

            var resolver = new FlightModelGeometryResolver(new[] { root }, false);
            Equal(true, resolver.TryResolve("TestJet Default", "unknown", "", out var byTitle), "title geometry lookup");
            Near(-14, byTitle.MainGearLongitudinalFeet, 1e-12, "title geometry arm");
            Equal(false, resolver.TryResolve("Unknown title", "unknown", "TJET", out _), "unknown title fails closed");
            Equal(false, resolver.TryResolve("GuidJet", "unknown", "", out _), "GUID-only attachment fails closed");
            Equal(true, resolver.TryResolve("BaseJet Livery", "unknown", "", out var byBaseContainer), "cross-package base-container lookup");
            Near(-9, byBaseContainer.MainGearLongitudinalFeet, 1e-12, "cross-package base-container arm");

            var duplicateBase = Path.Combine(
                root,
                "Community",
                "duplicate-base-package",
                "SimObjects",
                "Airplanes",
                "BaseJet");
            Directory.CreateDirectory(duplicateBase);
            File.WriteAllText(
                Path.Combine(duplicateBase, "aircraft.cfg"),
                "[FLTSIM.0]\ntitle = \"Other BaseJet\"\nisUserSelectable = 1\n");
            File.WriteAllText(
                Path.Combine(duplicateBase, "flight_model.cfg"),
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 20, 0, -8\n" +
                "point.1 = 1, -100, -8, -8\n" +
                "point.2 = 1, -100, 8, -8\n");
            var ambiguousBaseResolver = new FlightModelGeometryResolver(new[] { root }, false);
            Equal(
                false,
                ambiguousBaseResolver.TryResolve("BaseJet Livery", "unknown", "", out _),
                "ambiguous cross-package base-container fails closed");

            var sharedPackage = Path.Combine(root, "Community", "shared-attachment-package");
            var sharedGear = Path.Combine(
                sharedPackage,
                "SimObjects",
                "Airplanes",
                "SharedJet",
                "attachments",
                "vendor",
                "SharedGear",
                "config");
            Directory.CreateDirectory(sharedGear);
            File.WriteAllText(Path.Combine(sharedPackage, "manifest.json"), "{\"content_type\":\"AIRCRAFT\"}");
            File.WriteAllText(
                Path.Combine(sharedGear, "flight_model.cfg"),
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, -20, -12, -9\n" +
                "point.1 = 1, -20, 12, -9\n");

            var externalRoot = Path.Combine(
                root,
                "Community",
                "external-aircraft-package",
                "SimObjects",
                "Airplanes",
                "ExternalJet");
            var externalCommon = Path.Combine(externalRoot, "common", "config");
            var externalPreset = Path.Combine(externalRoot, "presets", "vendor", "default", "config");
            Directory.CreateDirectory(externalCommon);
            Directory.CreateDirectory(externalPreset);
            File.WriteAllText(
                Path.Combine(externalCommon, "flight_model.cfg"),
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 25, 0, -9\n" +
                "point.1 = 1, -10, -10, -9\n" +
                "point.2 = 1, -10, 10, -9\n");
            File.WriteAllText(
                Path.Combine(externalPreset, "aircraft.cfg"),
                "[GENERAL]\ntitle = \"ExternalJet\"\n");
            File.WriteAllText(
                Path.Combine(externalPreset, "attached_objects.cfg"),
                "[SIM_ATTACHMENT.0]\n" +
                "alias = \"SharedGear\"\n" +
                "attachment_root = \"SimObjects/Airplanes/SharedJet/attachments/vendor/SharedGear\"\n");
            var externalResolver = new FlightModelGeometryResolver(new[] { root }, false);
            Equal(
                true,
                externalResolver.TryResolve("ExternalJet", "unknown", "", out var external),
                "cross-package VFS attachment lookup");
            Near(-15, external.MainGearLongitudinalFeet, 1e-12, "cross-package VFS attachment arm");

            var lateRoot = Path.Combine(root, "late-packages");
            var lateResolver = new FlightModelGeometryResolver(new[] { lateRoot }, false);
            Equal(0, lateResolver.CatalogForTesting().Count, "initial late-package catalog");
            var lateAircraft = Path.Combine(lateRoot, "Community", "late-aircraft", "SimObjects", "Airplanes", "LateJet");
            Directory.CreateDirectory(lateAircraft);
            File.WriteAllText(
                Path.Combine(lateAircraft, "aircraft.cfg"),
                "[FLTSIM.0]\ntitle = \"LateJet\"\nisUserSelectable = 1\n");
            File.WriteAllText(
                Path.Combine(lateAircraft, "flight_model.cfg"),
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 20, 0, -8\n" +
                "point.1 = 1, -7, -8, -8\n" +
                "point.2 = 1, -7, 8, -8\n");
            Equal(true, lateResolver.TryResolve("LateJet", "unknown", "", out var late), "catalog refreshes after title miss");
            Near(-7, late.MainGearLongitudinalFeet, 1e-12, "late-package arm");

            IReadOnlyList<string> discoveredRoots = Array.Empty<string>();
            var discoveredResolver = new FlightModelGeometryResolver(
                Array.Empty<string>(),
                false,
                () => discoveredRoots);
            var discoveredRoot = Path.Combine(root, "discovered-after-start");
            var discoveredAircraft = Path.Combine(
                discoveredRoot,
                "Community",
                "discovered-aircraft",
                "SimObjects",
                "Airplanes",
                "DiscoveredJet");
            Directory.CreateDirectory(discoveredAircraft);
            File.WriteAllText(
                Path.Combine(discoveredAircraft, "aircraft.cfg"),
                "[FLTSIM.0]\ntitle = \"DiscoveredJet\"\nisUserSelectable = 1\n");
            File.WriteAllText(
                Path.Combine(discoveredAircraft, "flight_model.cfg"),
                "[CONTACT_POINTS]\n" +
                "point.0 = 1, 20, 0, -8\n" +
                "point.1 = 1, -6, -8, -8\n" +
                "point.2 = 1, -6, 8, -8\n");
            discoveredRoots = new[] { discoveredRoot };
            Equal(
                true,
                discoveredResolver.TryResolve("DiscoveredJet", "unknown", "", out var discovered),
                "refresh discovers a package root created after startup");
            Near(-6, discovered.MainGearLongitudinalFeet, 1e-12, "newly discovered package-root arm");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void FlightModelAsyncFailuresFallBackToTelemetry()
    {
        var resolver = new FlightModelGeometryResolver(
            Array.Empty<string>(),
            false,
            () => throw new IOException("simulated package discovery failure"));
        var options = resolver.CreateAnalysisOptionsAsync(
                "Missing aircraft",
                "unknown",
                string.Empty,
                Array.Empty<TelemetrySample>())
            .GetAwaiter()
            .GetResult();
        Equal<TouchdownAnalysisOptions?>(null, options, "optional geometry failure did not fall back");
    }

    private static List<TelemetrySample> ReconstructionSamples(
        double contactTime,
        double rawLatchFpm,
        int firstMainIndex = 1,
        int secondMainIndex = 2,
        int noseIndex = 0,
        double secondMainDelaySeconds = 0.0,
        double noseContactTime = 0.30,
        double sampleEndTime = 0.70)
    {
        var samples = new List<TelemetrySample>();
        var finalIndex = (int)Math.Ceiling((sampleEndTime + 0.30) / 0.025);
        for (var index = 0; index <= finalIndex; index++)
        {
            var time = -0.30 + index * 0.025;
            var x = time - contactTime;
            var onGround = time >= 0.025 - 1e-9;
            var pitchDegrees = PitchRadians(x) * 180.0 / Math.PI;
            if (onGround)
            {
                pitchDegrees += 2.5 * (time - 0.025);
            }

            var sample = new TelemetrySample
            {
                Sequence = index,
                SimulationTimeSeconds = time,
                OnGround = onGround,
                TouchdownNormalVelocityFps = onGround ? rawLatchFpm / 60.0 : 0.0,
                VelocityWorldYFps = Velocity(x),
                GroundAltitudeFeet = GroundAltitude(x),
                PlaneAltitudeFeet = GroundAltitude(x) + 10.0,
                RotationVelocityBodyXRadiansPerSecond = PitchRate(x),
                PitchDegrees = pitchDegrees,
                BankDegrees = BankRadians(x) * 180.0 / Math.PI,
            };
            if (onGround)
            {
                sample.ContactPointOnGround[firstMainIndex] = true;
                sample.ContactPointCompression[firstMainIndex] = (time - contactTime) * 100.0;
                if (time >= 0.025 + secondMainDelaySeconds - 1e-9)
                {
                    sample.ContactPointOnGround[secondMainIndex] = true;
                    sample.ContactPointCompression[secondMainIndex] =
                        (time - contactTime - secondMainDelaySeconds) * 100.0;
                }

                if (time >= noseContactTime - 1e-9)
                {
                    sample.ContactPointOnGround[noseIndex] = true;
                    sample.ContactPointCompression[noseIndex] = (time - noseContactTime) * 100.0;
                }
            }

            samples.Add(sample);
        }

        return samples;
    }

    private static List<TelemetrySample> DenseShortHistoryReconstructionSamples()
    {
        const double contactTime = 0.010;
        var samples = new List<TelemetrySample>();
        for (var index = 0; index < 5; index++)
        {
            var time = -0.050 + index * 0.0125;
            var x = time - contactTime;
            samples.Add(new TelemetrySample
            {
                Sequence = index,
                SimulationTimeSeconds = time,
                OnGround = false,
                VelocityWorldYFps = Velocity(x),
                GroundAltitudeFeet = GroundAltitude(x),
                PlaneAltitudeFeet = GroundAltitude(x) + 10.0,
                RotationVelocityBodyXRadiansPerSecond = PitchRate(x),
                PitchDegrees = PitchRadians(x) * 180.0 / Math.PI,
                BankDegrees = BankRadians(x) * 180.0 / Math.PI,
            });
        }

        var ground = ReconstructionSamples(contactTime, 280.0).Where(sample => sample.OnGround);
        samples.AddRange(ground);
        return samples;
    }

    private static TelemetrySample CloneTelemetrySample(TelemetrySample source)
    {
        return new TelemetrySample
        {
            Sequence = source.Sequence + 1000,
            HostElapsedSeconds = source.HostElapsedSeconds,
            SimulationTimeSeconds = source.SimulationTimeSeconds,
            OnGround = source.OnGround,
            VelocityWorldYFps = source.VelocityWorldYFps,
            GroundAltitudeFeet = source.GroundAltitudeFeet,
            PlaneAltitudeFeet = source.PlaneAltitudeFeet,
            RotationVelocityBodyXRadiansPerSecond = source.RotationVelocityBodyXRadiansPerSecond,
            PitchDegrees = source.PitchDegrees,
            BankDegrees = source.BankDegrees,
        };
    }

    private static List<TelemetrySample> SparseReconstructionSamples(int airbornePointCount)
    {
        var full = ReconstructionSamples(0.010, 280.0);
        var airborne = full.Where(sample => !sample.OnGround).ToList();
        var selected = airborne.Skip(Math.Max(0, airborne.Count - airbornePointCount));
        return selected.Concat(full.Where(sample => sample.OnGround)).ToList();
    }

    private static double Velocity(double x) => -4.0 + 2.0 * x + 3.0 * x * x;

    private static double GroundAltitude(double x) => 100.0 + 0.4 * x + 0.2 * x * x;

    private static double GroundDerivative(double x) => 0.4 + 0.4 * x;

    private static double PitchRate(double x) => 0.01 + 0.02 * x - 0.01 * x * x;

    private static double PitchRadians(double x) => 0.08 + 0.01 * x + 0.01 * x * x;

    private static double BankRadians(double x) => 0.02 - 0.005 * x;

    private static void ColumnarV7IsSmallerThanObjectLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-size-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var legacyRoot = Path.Combine(root, "v6");
            var columnarRoot = Path.Combine(root, "v7");
            var legacy = RepresentativeRecord(6, false);
            var columnar = RepresentativeRecord(7, true);
            var legacyPath = new LandingRepository(legacyRoot).Save(legacy);
            var columnarPath = new LandingRepository(columnarRoot).Save(columnar);
            var legacyBytes = new FileInfo(legacyPath).Length;
            var columnarBytes = new FileInfo(columnarPath).Length;
            if (columnarBytes >= legacyBytes)
            {
                throw new InvalidOperationException($"columnar detail is not smaller: v7={columnarBytes}, v6={legacyBytes}");
            }

            Console.WriteLine($"  storage sample: v6 {legacyBytes:N0} bytes, v7 {columnarBytes:N0} bytes ({100.0 * columnarBytes / legacyBytes:F1}%)");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void BounceHistoryShowsLatestContactFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-bounce-order-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var timestamp = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
            var repository = new LandingRepository(root);
            repository.Save(new LandingRecord { Id = "contact-1", TimestampUtc = timestamp, ContactNumber = 1, ContactCount = 2 });
            repository.Save(new LandingRecord { Id = "contact-2", TimestampUtc = timestamp, ContactNumber = 2, ContactCount = 2 });
            var history = new LandingRepository(root).LoadAll();
            Equal(2, history[0].ContactNumber, "top history row contact number");
            Equal(1, history[1].ContactNumber, "second history row contact number");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static LandingRecord RepresentativeRecord(int formatVersion, bool compactControllers)
    {
        var record = new LandingRecord
        {
            FormatVersion = formatVersion,
            Id = "size-" + formatVersion,
            TimestampUtc = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc),
            AircraftTitle = "Representative aircraft",
        };
        if (compactControllers) record.RawControllerSourceIndices.Add(5);
        for (var index = 0; index < 600; index++)
        {
            var rawWidth = compactControllers ? 1 : TelemetrySample.CapturedControllerCount;
            var rawValues = new double[rawWidth];
            var rawValid = new bool[rawWidth];
            var rawAges = new double[rawWidth];
            rawValues[compactControllers ? 0 : 5] = Math.Sin(index * 0.1) * 30;
            rawValid[compactControllers ? 0 : 5] = true;
            record.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = -15 + index * 0.05,
                InertialFpm = 200 + Math.Sin(index * 0.03) * 50,
                IndicatedFpm = 220 + Math.Sin(index * 0.02) * 45,
                GForce = 1 + Math.Sin(index * 0.05) * 0.1,
                AglFeet = Math.Max(0, 500 - index),
                PitchDegrees = 4 + Math.Sin(index * 0.01),
                PilotPitchPercent = Math.Sin(index * 0.1) * 30,
                WindSpeedKnots = 12,
                WindDirectionDegrees = 240,
                OnGround = index >= 300,
                RawControllerYAxisPercent = rawValues,
                RawControllerYAxisValid = rawValid,
                RawControllerYAxisAgeSeconds = rawAges,
            });
        }

        return record;
    }

    private static TelemetrySample ApproachSample(double timeSeconds)
    {
        return new TelemetrySample
        {
            SimulationTimeSeconds = timeSeconds,
            MotionSimulation = true,
            OnGround = false,
            AboveGroundLevelFeet = 400,
            VelocityWorldYFps = -5,
        };
    }

    private static List<TelemetrySample> ReplayDetectionSamples(bool replay)
    {
        const double latitudeDegrees = 42.0;
        const double groundSpeedKnots = 120.0;
        const double verticalRateFps = -10.0;
        var longitudeDegreesPerSecond = groundSpeedKnots /
                                        (3600.0 * 60.0 * Math.Cos(latitudeDegrees * Math.PI / 180.0));
        var samples = new List<TelemetrySample>();
        for (var index = 0; index <= 170; index++)
        {
            var time = -16.0 + index * 0.1;
            samples.Add(new TelemetrySample
            {
                Sequence = index,
                SimulationTimeSeconds = time,
                MotionSimulation = true,
                OnGround = time >= -1e-9,
                LatitudeDegrees = latitudeDegrees,
                LongitudeDegrees = 23.0 + (time + 16.0) * longitudeDegreesPerSecond,
                PlaneAltitudeFeet = 500.0 + verticalRateFps * time,
                GroundAltitudeFeet = 500.0,
                AboveGroundLevelFeet = Math.Max(0.0, -verticalRateFps * time),
                GroundSpeedKnots = replay ? 0.1 : groundSpeedKnots,
                IndicatedAirspeedKnots = replay ? 1.0 : 125.0,
                VelocityWorldYFps = replay ? -0.2 : verticalRateFps,
            });
        }

        return samples;
    }

    private static IDisposable NewRecorder()
    {
        var recorder = Activator.CreateInstance(
            RecorderType(),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new object[] { IntPtr.Zero },
            null);
        return (IDisposable)(recorder ?? throw new InvalidOperationException("Recorder construction failed."));
    }

    private static Type RecorderType()
    {
        return typeof(MainWindow).Assembly.GetType(
                   "LandingStats.App.Telemetry.SimConnectLandingRecorder",
                   true)
               ?? throw new TypeLoadException("SimConnectLandingRecorder");
    }

    private static object ReplayFlowEvent(string name)
    {
        var assembly = Assembly.Load("Microsoft.FlightSimulator.SimConnect");
        var eventType = assembly.GetType(
                            "Microsoft.FlightSimulator.SimConnect.SIMCONNECT_RECV_FLOW_EVENT",
                            true)
                        ?? throw new TypeLoadException("SIMCONNECT_RECV_FLOW_EVENT");
        var valueType = assembly.GetType(
                            "Microsoft.FlightSimulator.SimConnect.SIMCONNECT_FLOW_EVENT",
                            true)
                        ?? throw new TypeLoadException("SIMCONNECT_FLOW_EVENT");
        var instance = Activator.CreateInstance(eventType)
                       ?? throw new InvalidOperationException("Could not construct replay flow event.");
        eventType.GetField("FlowEvent")!.SetValue(instance, Enum.Parse(valueType, name));
        return instance;
    }

    private static object? Field(object target, string name)
    {
        return target.GetType().GetField(name, InstancePrivate)?.GetValue(target)
               ?? (target.GetType().GetField(name, InstancePrivate) == null
                   ? throw new MissingFieldException(target.GetType().FullName, name)
                   : null);
    }

    private static object? RecorderProperty(object target, string name)
    {
        return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target)
               ?? throw new MissingMemberException(target.GetType().FullName, name);
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, InstancePrivate)
                    ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static void Invoke(object target, string name, params object[] arguments)
    {
        var method = target.GetType().GetMethod(name, InstancePrivate | BindingFlags.Public)
                     ?? throw new MissingMethodException(target.GetType().FullName, name);
        method.Invoke(target, arguments);
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failures++;
            var failure = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            Console.Error.WriteLine($"FAIL {name}: {failure}");
        }
    }

    private static void Equal<T>(T expected, object? actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, (T)actual!))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"{message}: expected {typeof(TException).Name}");
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                $"{message}: expected {expected:R} ± {tolerance:R}, got {actual:R}");
        }
    }

    private static void Same(object expected, object? actual, string message)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: references differ");
        }
    }

    private static void NotSame(object first, object? second, string message)
    {
        if (ReferenceEquals(first, second))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Null(object? value, string message)
    {
        if (value != null)
        {
            throw new InvalidOperationException($"{message}: expected null");
        }
    }

    private static void NotNull(object? value, string message)
    {
        if (value == null)
        {
            throw new InvalidOperationException($"{message}: expected a value");
        }
    }

    private static void AssertNoStorageSentinel(object? value)
    {
        if (value == null || value is string || value is DateTime)
        {
            return;
        }

        if (value is double number)
        {
            if (number == LandingRecord.NonFiniteStorageSentinel)
            {
                throw new InvalidOperationException("storage sentinel leaked into a restored record");
            }

            return;
        }

        if (value is IEnumerable sequence)
        {
            foreach (var item in sequence)
            {
                AssertNoStorageSentinel(item);
            }

            return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum)
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetCustomAttribute<DataMemberAttribute>() != null)
            {
                AssertNoStorageSentinel(property.GetValue(value));
            }
        }
    }

    private sealed class UpdateFixtureHandler : HttpMessageHandler
    {
        private readonly byte[] _manifest;
        private readonly byte[] _signature;

        public List<string> RequestPaths { get; } = new List<string>();

        public UpdateFixtureHandler(string manifest, string signature)
        {
            _manifest = Encoding.UTF8.GetBytes(manifest);
            _signature = Encoding.ASCII.GetBytes(signature + "\n");
        }

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            var bytes = request.RequestUri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                ? _signature
                : _manifest;
            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(bytes),
            });
        }
    }

    private sealed class TelemetryEnrollmentFixtureHandler : HttpMessageHandler
    {
        private int _captureCount;
        public string? EnrollmentPayload { get; private set; }
        public int EnrollmentCount { get; private set; }
        public int CaptureCount => Volatile.Read(ref _captureCount);

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/v1/config", StringComparison.Ordinal))
            {
                return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        "{\"protocol\":1,\"registration_mode\":\"open\",\"telemetry_schema\":5,\"max_upload_bytes\":16777216}",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/v1/enroll", StringComparison.Ordinal))
            {
                EnrollmentCount++;
                EnrollmentPayload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"status\":\"enrolled\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/v1/captures", StringComparison.Ordinal))
            {
                _ = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                Interlocked.Increment(ref _captureCount);
                return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"status\":\"accepted\"}", Encoding.UTF8, "application/json"),
                });
            }

            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
            });
        }
    }

    private sealed class TelemetryRecoveringFixtureHandler : HttpMessageHandler
    {
        private int _remainingEnrollmentFailures;
        private int _forbidFirstCapture;
        private int _enrollmentCount;
        private int _captureCount;

        public TelemetryRecoveringFixtureHandler(
            int enrollmentFailures = 0,
            bool forbidFirstCapture = false)
        {
            _remainingEnrollmentFailures = enrollmentFailures;
            _forbidFirstCapture = forbidFirstCapture ? 1 : 0;
        }

        public int EnrollmentCount => Volatile.Read(ref _enrollmentCount);
        public int CaptureCount => Volatile.Read(ref _captureCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.EndsWith("/v1/config", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        "{\"protocol\":1,\"registration_mode\":\"open\",\"telemetry_schema\":5,\"max_upload_bytes\":16777216}",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/v1/enroll", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _enrollmentCount);
                if (Interlocked.Decrement(ref _remainingEnrollmentFailures) >= 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        RequestMessage = request,
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"status\":\"enrolled\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/v1/captures", StringComparison.Ordinal))
            {
                _ = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                Interlocked.Increment(ref _captureCount);
                if (Interlocked.Exchange(ref _forbidFirstCapture, 0) == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        RequestMessage = request,
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"status\":\"accepted\"}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
            });
        }
    }

    private sealed class TelemetryRejectingFixtureHandler : HttpMessageHandler
    {
        public int CaptureCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.EndsWith("/v1/config", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        "{\"protocol\":1,\"registration_mode\":\"open\",\"telemetry_schema\":5,\"max_upload_bytes\":16777216}",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/v1/enroll", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"status\":\"enrolled\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/v1/captures", StringComparison.Ordinal))
            {
                CaptureCount++;
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)422)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"detail\":\"invalid capture\"}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
            });
        }
    }
}

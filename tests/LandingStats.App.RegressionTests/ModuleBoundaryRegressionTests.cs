using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ModuleBoundaryRegressionTests
{
    public static void RunAll() => Task.Run(async () =>
    {
        foreach (var assembly in new[] { typeof(LandingRecord).Assembly, typeof(LandingRepository).Assembly,
            typeof(LandingEpisodeCoordinator).Assembly, typeof(LandingRuntime).Assembly,
            typeof(AnalysisWorkerRole).Assembly, typeof(UploadWorkerRole).Assembly, typeof(CloudWorkerRole).Assembly })
        {
            Check(assembly != typeof(App).Assembly, "A backend component is still compiled into the UI executable.");
            Check(!assembly.GetReferencedAssemblies().Any(reference => reference.Name == "MSFS-Landing-Stats" ||
                reference.Name == "PresentationFramework" || reference.Name == "PresentationCore" || reference.Name == "Microsoft.FlightSimulator.SimConnect"),
                "A backend module depends on WPF or the native simulator SDK: " + assembly.GetName().Name);
        }
        var root = Path.Combine(Path.GetTempPath(), "ls-module-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var engine = ProcessingEngineLease.Capture(Path.Combine(root, "engines"), AppDomain.CurrentDomain.BaseDirectory);
            Check(!File.Exists(Path.Combine(engine.DirectoryPath, "MSFS-Landing-Stats.exe")), "Engine identity still includes the presentation executable.");
            foreach (var role in new[] { ComponentRole.Analysis, ComponentRole.Upload, ComponentRole.CloudSync })
            {
                ProcessWorkerHost? host = null;
                using (host = new ProcessWorkerHost(Path.Combine(engine.DirectoryPath, "LandingStats.WorkerHost.exe"),
                    new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, role),
                    new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency),
                    (message, _) => host!.AcknowledgeAsync(message.Sequence)))
                {
                    await host.StartAsync(TimeSpan.FromSeconds(10));
                    await host.StopAsync(TimeSpan.FromSeconds(3));
                    Check(host.Exit.IsCompleted, "Production role did not exit without a WPF assembly: " + role);
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }).GetAwaiter().GetResult();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

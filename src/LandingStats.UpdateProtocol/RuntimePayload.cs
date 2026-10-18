using System;

namespace LandingStats.Packaging;

// Build scripts read this same explicit list. Add runtime dependencies here
// deliberately; stale DLLs in a build directory never become release inputs.
public static class RuntimePayload
{
    public static readonly string[] AllowedFiles =
    {
        "MSFS-Landing-Stats.exe",
        "MSFS-Landing-Stats.exe.config",
        "LandingStats.WorkerHost.exe",
        "LandingStats.WorkerHost.exe.config",
        "LandingStats.Core.dll",
        "LandingStats.Archives.dll",
        "LandingStats.Runtime.Contracts.dll",
        "LandingStats.Runtime.dll",
        "LandingStats.Persistence.dll",
        "LandingStats.Delivery.dll",
        "LandingStats.Recording.dll",
        "LandingStats.Storage.dll",
        "LandingStats.Models.dll",
        "LandingStats.Workers.Common.dll",
        "LandingStats.Workers.Simulator.dll",
        "LandingStats.Workers.Controllers.dll",
        "LandingStats.Workers.Analysis.dll",
        "LandingStats.Workers.Upload.dll",
        "LandingStats.Workers.Cloud.dll",
        "LandingStats.Workers.IntegrationApi.dll",
        "Microsoft.FlightSimulator.SimConnect.dll",
        "SimConnect.dll",
        "Google.Apis.Auth.dll",
        "Google.Apis.Core.dll",
        "Google.Apis.dll",
        "Google.Apis.Drive.v3.dll",
        "Newtonsoft.Json.dll",
        "System.Buffers.dll",
        "System.Memory.dll",
        "System.Numerics.Vectors.dll",
        "System.Runtime.CompilerServices.Unsafe.dll",
        "System.Threading.Tasks.Extensions.dll",
        "ZstdSharp.dll",
    };
    public static bool IsAllowed(string name) => Array.Exists(AllowedFiles, file => string.Equals(file, name, StringComparison.OrdinalIgnoreCase));
}

using System;
using System.Reflection;
using LandingStats.App.Models;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class BuildVersionRegressionTests
{
    internal static void RunAll()
    {
        foreach (var assembly in new[] { typeof(LandingRecord).Assembly, typeof(LandingStats.App.Updater.Program).Assembly })
        {
            var version = assembly.GetName().Version!.ToString(3);
            var information = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (information == null || !information.StartsWith(version + "-", StringComparison.Ordinal))
                throw new InvalidOperationException("Informational version must follow the release version: " + assembly.GetName().Name);
        }
        var appVersion = typeof(LandingRecord).Assembly.GetName().Version!.ToString(3);
        if (!RawCaptureRepository.ApplicationVersion().StartsWith(appVersion + "-", StringComparison.Ordinal))
            throw new InvalidOperationException("Raw telemetry must report the current release version.");
    }
}

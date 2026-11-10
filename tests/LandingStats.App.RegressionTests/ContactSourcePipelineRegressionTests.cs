using System;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class ContactSourcePipelineRegressionTests
{
    public static void RunAll()
    {
        using var fixture = new ReportPipelineFixture();
        foreach (var scenario in ContactSourceAnalysisRegressionTests.Scenarios())
        {
            var numeric = scenario.Samples.SelectMany(TelemetryBinaryCodec.Encode).ToArray();
            var source = ReplaySourceTimelineCodec.Encode(scenario.Samples)!;
            var episode = new LandingEpisodeEventArgs(1, scenario.Samples, "MSFS", "TEST", "TEST", "TEST",
                Array.Empty<AirportFacility>(), Array.Empty<string>(), persistentId: Guid.NewGuid());
            var manifest = fixture.Accept(episode);
            var input = fixture.Store.ReadInputs(manifest);
            var geometry = ReportPipelineFixture.Complete(fixture.Processor.ResolveGeometryAsync(input, manifest));
            var result = ReportPipelineFixture.Complete(fixture.Processor.ProcessAsync(input.Episode,
                Array.Empty<AirportFacility>(), DateTime.UtcNow, persistHistory: false, geometryInputs: geometry));
            Check(result.Records.Count == scenario.Expected, scenario.Name + ": episode processor contact count");
            Check(numeric.SequenceEqual(scenario.Samples.SelectMany(TelemetryBinaryCodec.Encode)) &&
                source.SequenceEqual(ReplaySourceTimelineCodec.Encode(scenario.Samples)!), scenario.Name + ": pipeline changed source input");
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

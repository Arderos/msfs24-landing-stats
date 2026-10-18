using System;
using LandingStats.App.RegressionTests;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--corpus-fingerprints") return CorpusFingerprintCheck.Run(args[1], args[2]);
            if (args.Length == 3 && args[0] == "--dump-corpus") return CorpusFingerprintCheck.Dump(args[1], args[2]);
            BinaryTelemetryRegressionTests.RunAll();
            Console.WriteLine("PASS binary telemetry and strict snapshot versions");
            Console.WriteLine("fingerprint=" + BinaryTelemetryRegressionTests.GoldenFingerprint());
            RuntimeProtocolRegressionTests.RunAll();
            ReplaySourceCoreRegressionTests.RunAll();
            Console.WriteLine("PASS source observation codec, legacy unknown and external-control evidence boundaries");
            Console.WriteLine("PASS versioned protocol, persisted identities and handshake");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}

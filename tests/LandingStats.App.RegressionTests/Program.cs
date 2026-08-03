using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using LandingStats.App;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class Program
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _failures;

    private static int Main()
    {
        Run("deduplicator keeps the last same-time frame", DeduplicatorKeepsLastFrame);
        Run("approach timeout re-arms capture", ApproachTimeoutRearmsCapture);
        Run("raw debug rotates at the frame limit", RawDebugRotatesAtLimit);
        Run("valid frame clears transient error state", ValidFrameClearsTransientErrorState);
        Run("legacy SimConnect controller path is absent", LegacyControllerPathIsAbsent);

        Console.WriteLine(_failures == 0
            ? "All regression tests passed."
            : $"{_failures} regression test(s) failed.");
        return _failures == 0 ? 0 : 1;
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

    private static void RawDebugRotatesAtLimit()
    {
        using var recorder = NewRecorder();
        Invoke(recorder, "SetRawDebugEnabled", true);
        var original = (IList)(Field(recorder, "_rawDebugSamples") ?? throw new InvalidOperationException("RAW list was not created."));
        var limitField = RecorderType().GetField("RawDebugChunkMaximumSamples", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("RawDebugChunkMaximumSamples");
        var limit = (int)(limitField.GetRawConstantValue() ?? 0);
        var sample = new TelemetrySample();
        while (original.Count < limit - 1)
        {
            original.Add(sample);
        }

        Invoke(recorder, "AddRawDebugSample", sample);
        var rotated = (IList)(Field(recorder, "_rawDebugSamples") ?? throw new InvalidOperationException("Rotated RAW list was not created."));
        NotSame(original, rotated, "RAW list should rotate at the limit");
        Equal(0, rotated.Count, "new RAW chunk should begin empty");
        Equal(true, RecorderProperty(recorder, "RawDebugEnabled"), "RAW mode should stay enabled after rotation");
    }

    private static void ValidFrameClearsTransientErrorState()
    {
        using var recorder = NewRecorder();
        SetField(recorder, "_recoverStatusAfterFrame", true);
        Invoke(recorder, "RecoverStatusAfterFrame");
        Equal(false, Field(recorder, "_recoverStatusAfterFrame"), "transient error recovery flag");
    }

    private static void LegacyControllerPathIsAbsent()
    {
        var type = RecorderType();
        Null(type.GetMethod("OnRecvControllersList", InstancePrivate), "legacy controller-list handler");
        Null(type.GetNestedType("InputGroups", BindingFlags.NonPublic), "legacy input-group enum");
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
}

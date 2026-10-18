namespace LandingStats.Runtime.Contracts;

public static class RuntimeSequence
{
    public static bool IsNext(long previous, RuntimeEnvelope envelope)
    {
        if (envelope.Payload is SourceGapMessage gap && gap.LossCountKnown)
            return previous < long.MaxValue && gap.FirstMissing == previous + 1 &&
                gap.LastMissing < long.MaxValue && envelope.Sequence == gap.LastMissing + 1;
        return previous < long.MaxValue && envelope.Sequence == previous + 1;
    }
}

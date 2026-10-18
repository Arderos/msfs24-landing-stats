namespace LandingStats.Core;

/// <summary>A contact requires airborne evidence in the same uninterrupted physics segment.</summary>
public sealed class TelemetryContactContinuity
{
    private bool _airborne;

    public bool Observe(TelemetrySample sample)
    {
        var contact = _airborne && sample.MotionSimulation && sample.OnGround;
        _airborne = sample.MotionSimulation && !sample.OnGround;
        return contact;
    }

    public void Reset() => _airborne = false;
    public bool HasAirborneWitness => _airborne;
    public void RestoreAirborneWitness(bool airborne) => _airborne = airborne;
}

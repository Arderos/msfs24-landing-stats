using System.Collections.Generic;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Controls;

/// <summary>Applies pure suspension decisions to each view's WPF transforms.</summary>
internal sealed class ReplayGearRegistration
{
    private readonly ReplayLandingGear _gear;
    private readonly ReplayGearState _state = new ReplayGearState();
    private readonly ReplayGearRegistrationModel _model;
    internal bool IsCalibrated => _model.IsCalibrated;
    internal double UnloadedExtension => _model.UnloadedExtension;
    internal static double VisualOffset(LandingReplayTrack track, TelemetryDecisionJournal? journal = null) => ReplayGearRegistrationModel.VisualOffset(track, journal);
    internal ReplayGearRegistration(ReplayLandingGear gear, LandingReplayTrack track, LandingReplaySurface surface, double visualOffset,
        TelemetryDecisionJournal? journal = null, IEnumerable<double>? contactTimesSeconds = null)
    {
        _gear = gear;
        _model = new ReplayGearRegistrationModel(_state, track, surface.Geometry, visualOffset, journal, contactTimesSeconds);
    }
    internal void Update(LandingReplayPose pose)
    {
        _model.Update(pose);
        for (var i = 0; i < _gear.Legs.Count; i++) _gear.Legs[i].SetExtension(_state.Legs[i].Extension);
    }
    internal void RecordFrameDecisions() => _model.RecordFrameDecisions();
    internal double ClearanceAt(ReplayLandingGear.Leg leg, LandingReplayPose pose)
    {
        for (var i = 0; i < _gear.Legs.Count; i++)
            if (ReferenceEquals(_gear.Legs[i], leg)) return _model.ClearanceAt(_state.Legs[i], pose);
        throw new System.ArgumentException("Unknown gear leg.", nameof(leg));
    }
}

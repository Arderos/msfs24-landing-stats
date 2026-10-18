using System;
using System.IO;
using LandingStats.App.Runtime;

namespace LandingStats.App;

public partial class MainWindow
{
    private void RecordPresentationPhase(Guid id, EpisodeTimelinePhase phase, int count = 0) =>
        EpisodeTimeline.Mark(Path.Combine(Path.GetDirectoryName(_repository.RootPath)!, "Runtime"), id, phase, count);
}

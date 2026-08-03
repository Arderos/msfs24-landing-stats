using System.Collections.Generic;
using System.Runtime.Serialization;

namespace LandingStats.App.Models;

[DataContract]
public sealed class LandingContactSeries
{
    [DataMember(Order = 1)]
    public int ContactPointIndex { get; set; }

    [DataMember(Order = 2)]
    public List<LandingContactPoint> Points { get; set; } = new List<LandingContactPoint>();
}

[DataContract]
public sealed class LandingContactPoint
{
    [DataMember(Order = 1)]
    public double TimeSeconds { get; set; }

    [DataMember(Order = 2)]
    public double CompressionPercent { get; set; }

    [DataMember(Order = 3)]
    public double PositionPercent { get; set; }

    [DataMember(Order = 4)]
    public bool OnGround { get; set; }
}

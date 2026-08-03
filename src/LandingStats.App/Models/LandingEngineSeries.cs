using System.Collections.Generic;
using System.Runtime.Serialization;

namespace LandingStats.App.Models;

[DataContract]
public sealed class LandingEngineSeries
{
    [DataMember(Order = 1)]
    public int EngineNumber { get; set; }

    [DataMember(Order = 2)]
    public List<LandingEnginePoint> Points { get; set; } = new List<LandingEnginePoint>();
}

[DataContract]
public sealed class LandingEnginePoint
{
    [DataMember(Order = 1)]
    public double TimeSeconds { get; set; }

    [DataMember(Order = 2)]
    public double ThrottlePercent { get; set; }

    [DataMember(Order = 3)]
    public double N1Percent { get; set; }

    [DataMember(Order = 4)]
    public double Rpm { get; set; }

    [DataMember(Order = 5)]
    public double ReversePercent { get; set; }
}

using System.Runtime.Serialization;

namespace LandingStats.App.Models;

[DataContract]
public sealed class AirportFacility
{
    [DataMember(Order = 1)]
    public string Ident { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public string Region { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public double LatitudeDegrees { get; set; }

    [DataMember(Order = 4)]
    public double LongitudeDegrees { get; set; }

    [DataMember(Order = 5)]
    public double AltitudeMeters { get; set; }

    public string Key => $"{Ident.Trim().ToUpperInvariant()}|{Region.Trim().ToUpperInvariant()}";
}

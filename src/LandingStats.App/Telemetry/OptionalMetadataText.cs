using System;
using System.Text;

namespace LandingStats.App.Telemetry;

/// <summary>Optional native labels must never invalidate a required source stream.</summary>
internal static class OptionalMetadataText
{
    internal static string Sanitize(string? value, int maximumBytes, string fallback)
    {
        var result = new StringBuilder(); var bytes = 0;
        foreach (var raw in (value ?? string.Empty).Split('\0'))
        {
            for (var i = 0; i < raw.Length; i++)
            {
                var character = raw[i]; string unit;
                if (char.IsHighSurrogate(character) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
                    unit = raw.Substring(i++, 2);
                else unit = char.IsSurrogate(character) ? "\uFFFD" : character.ToString();
                var size = Encoding.UTF8.GetByteCount(unit);
                if (bytes + size > maximumBytes) return Finish(result, fallback);
                result.Append(unit); bytes += size;
            }
        }
        return Finish(result, fallback);
    }
    private static string Finish(StringBuilder value, string fallback)
    { var result = value.ToString().Trim(); return result.Length == 0 ? fallback : result; }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace LandingStats.App.Models;

/// <summary>Immutable presentation strings supplied by the application. The
/// model assembly never loads a UI resource dictionary or creates a dispatcher.</summary>
internal static class ModelText
{
    private static IReadOnlyDictionary<string, string> _strings = new Dictionary<string, string>(StringComparer.Ordinal);
    public static void SetStrings(IReadOnlyDictionary<string, string> strings) =>
        Volatile.Write(ref _strings, new Dictionary<string, string>(Copy(strings), StringComparer.Ordinal));
    private static IDictionary<string, string> Copy(IReadOnlyDictionary<string, string> strings)
    {
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in strings) copy.Add(pair.Key, pair.Value);
        return copy;
    }
    public static string Text(string key) => Volatile.Read(ref _strings).TryGetValue(key, out var value) ? value : key;
    public static string Format(string key, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, Text(key), arguments);
}

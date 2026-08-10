using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;

namespace LandingStats.App.Settings;

internal static class LocalizationManager
{
    public const string AutomaticLanguage = "auto";
    public const string EnglishLanguage = "en";
    public const string RussianLanguage = "ru";

    private static readonly CultureInfo DetectedSystemCulture = CultureInfo.CurrentCulture;
    private static readonly object DictionaryGate = new object();
    private static readonly Dictionary<string, LoadedDictionary> LoadedDictionaries =
        new Dictionary<string, LoadedDictionary>(StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, string> _strings =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static ResourceDictionary? _activeDictionary;

    public static string EffectiveLanguage { get; private set; } = EnglishLanguage;

    public static string NormalizePreference(string? preference)
    {
        var normalized = preference?.Trim().ToLowerInvariant();
        return normalized == EnglishLanguage || normalized == RussianLanguage
            ? normalized
            : AutomaticLanguage;
    }

    public static string ResolveLanguage(string? preference, CultureInfo systemCulture)
    {
        var normalized = NormalizePreference(preference);
        if (normalized != AutomaticLanguage)
        {
            return normalized;
        }

        return string.Equals(systemCulture.TwoLetterISOLanguageName, RussianLanguage, StringComparison.OrdinalIgnoreCase)
            ? RussianLanguage
            : EnglishLanguage;
    }

    public static CultureInfo CultureFor(string effectiveLanguage)
    {
        return string.Equals(effectiveLanguage, RussianLanguage, StringComparison.Ordinal)
            ? CultureInfo.GetCultureInfo("ru-RU")
            : CultureInfo.GetCultureInfo("en-GB");
    }

    public static void Apply(string? preference)
    {
        var effectiveLanguage = ResolveLanguage(preference, DetectedSystemCulture);
        var culture = CultureFor(effectiveLanguage);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        var loaded = LoadDictionary(effectiveLanguage);
        var dictionary = loaded.Dictionary;
        var strings = loaded.Strings;

        var resources = Application.Current?.Resources;
        if (resources != null)
        {
            if (_activeDictionary != null)
            {
                resources.MergedDictionaries.Remove(_activeDictionary);
            }

            // WPF resolves duplicate keys from the last merged dictionary.
            resources.MergedDictionaries.Add(dictionary);
            _activeDictionary = dictionary;
        }

        Volatile.Write(ref _strings, strings);
        EffectiveLanguage = effectiveLanguage;
    }

    public static string Text(string key)
    {
        var strings = Volatile.Read(ref _strings);
        return strings.TryGetValue(key, out var value) ? value : key;
    }

    public static string Format(string key, params object[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, Text(key), arguments);
    }

    private static LoadedDictionary LoadDictionary(string effectiveLanguage)
    {
        lock (DictionaryGate)
        {
            if (LoadedDictionaries.TryGetValue(effectiveLanguage, out var cached))
            {
                return cached;
            }

            var assemblyName = typeof(LocalizationManager).Assembly.GetName().Name
                               ?? throw new InvalidOperationException("Localization assembly name is unavailable.");
            var dictionary = new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/{assemblyName};component/Localization/Strings.{effectiveLanguage}.xaml",
                    UriKind.Absolute),
            };
            var strings = dictionary
                .Cast<DictionaryEntry>()
                .Where(entry => entry.Key is string && entry.Value is string)
                .ToDictionary(
                    entry => (string)entry.Key,
                    entry => (string)entry.Value,
                    StringComparer.Ordinal);
            var loaded = new LoadedDictionary(dictionary, strings);
            LoadedDictionaries.Add(effectiveLanguage, loaded);
            return loaded;
        }
    }

    private sealed class LoadedDictionary
    {
        public LoadedDictionary(ResourceDictionary dictionary, IReadOnlyDictionary<string, string> strings)
        {
            Dictionary = dictionary;
            Strings = strings;
        }

        public ResourceDictionary Dictionary { get; }
        public IReadOnlyDictionary<string, string> Strings { get; }
    }
}

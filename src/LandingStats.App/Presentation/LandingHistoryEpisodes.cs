using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Windows.Data;
using LandingStats.App.Models;

namespace LandingStats.App.Presentation;

// History grouping and destructive commands use explicit identity only. Replay's
// legacy timestamp matching is deliberately not a deletion contract.
internal static class LandingHistoryEpisodes
{
    public static bool SameEpisode(LandingRecord first, LandingRecord second) =>
        !string.IsNullOrWhiteSpace(first.RecordingEpisodeId) && first.IsImported == second.IsImported &&
        string.Equals(first.RecordingEpisodeId, second.RecordingEpisodeId, StringComparison.Ordinal);

    public static LandingRecord[] ForRecord(LandingRecord selected, IEnumerable<LandingRecord> records) =>
        records.Where(record => record.Id == selected.Id ||
            SameEpisode(selected, record))
            .OrderBy(record => record.ContactNumber).ThenBy(record => record.Id, StringComparer.Ordinal).ToArray();
}

public sealed class LandingHistoryEpisodeLayout
{
    public bool ConnectAbove { get; internal set; }
    public bool ConnectBelow { get; internal set; }
    public bool ShowConnector => ConnectAbove || ConnectBelow;
}

public sealed class LandingHistoryEpisodeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var layout = new LandingHistoryEpisodeLayout();
        if (values.Length < 2 || values[0] is not LandingRecord record || values[1] is not IReadOnlyList<LandingRecord> records)
            return layout;
        for (var index = 0; index < records.Count; index++)
        {
            if (records[index].Id != record.Id) continue;
            layout.ConnectAbove = index > 0 && LandingHistoryEpisodes.SameEpisode(record, records[index - 1]);
            layout.ConnectBelow = index + 1 < records.Count && LandingHistoryEpisodes.SameEpisode(record, records[index + 1]);
            break;
        }
        return layout;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

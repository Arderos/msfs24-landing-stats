using System;

namespace LandingStats.App.Models;

/// <summary>Shared sorted-time lookup. Exact ties consistently choose the earlier point.</summary>
internal static class RecordedTimeSearch
{
    internal static int LowerBound(int count, Func<int, double> timeAt, double target)
    {
        var low = 0; var high = count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (timeAt(middle) < target) low = middle + 1; else high = middle;
        }
        return low;
    }
    internal static int Nearest(int count, Func<int, double> timeAt, double target)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        var next = LowerBound(count, timeAt, target);
        if (next == 0) return 0;
        if (next == count) return count - 1;
        return target - timeAt(next - 1) <= timeAt(next) - target ? next - 1 : next;
    }
}

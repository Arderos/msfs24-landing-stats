using System;
using System.IO;

namespace LandingStats.App.Storage;

/// <summary>Per-package allocation allowances, independent of retained wire bytes.</summary>
internal sealed class LandingShareReadBudget
{
    // Operational admission budget for the decoded object graph and its working
    // projections. Opaque extension bytes have the separate archive byte limit.
    // Costs deliberately include object/array headers, collection growth and
    // temporary history-column copies; they are not a measured heap-size claim.
    internal const long MaximumBytes = 256L * 1024 * 1024;
    internal const int ObjectBytes = 4096;
    internal const int ArrayBytes = 64;
    internal const int ScalarBytes = 32;
    internal const int StringBytes = 64;
    internal const int TextByteCost = 4; // UTF-16 plus parsing/copying support.
    internal const int MaximumGraphItems = (int)(MaximumBytes / ScalarBytes);
    private long _remaining;

    internal LandingShareReadBudget(long maximumBytes = MaximumBytes)
    {
        if (maximumBytes < 0 || maximumBytes > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _remaining = maximumBytes;
    }

    internal void Reserve(long bytes)
    {
        if (bytes < 0 || bytes > _remaining)
            throw new InvalidDataException("The landing share exceeds its decoded object and projection memory budget.");
        _remaining -= bytes;
    }

    internal void ReserveContacts(int count) => Reserve(CheckedCount(count) * ObjectBytes);
    internal void ReserveBlocks(int count) => Reserve(CheckedCount(count) * 256);
    internal void ReserveTypeBytes(int byteCount) => Reserve(CheckedCount(byteCount) * TextByteCost);

    // Includes each point, its list slot/default arrays, and the compact columns
    // materialized again by history validation/publication. Controller channels
    // additionally need their per-row value/validity/age arrays and copies.
    internal void ReserveSeries(int count, int controllerWidth = 0) =>
        ReserveRows(count, 2048 + 64 * CheckedCount(controllerWidth));
    internal void ReserveInputs(int count, int controllerWidth) =>
        ReserveRows(count, 128 + 64 * CheckedCount(controllerWidth));
    internal void ReserveAuxiliaryPoints(int count) => Reserve(CheckedCount(count) * 256);
    internal void ReserveSourceObservations(int count) => Reserve(CheckedCount(count) * 1024);

    private void ReserveRows(int count, long bytesPerRow)
    {
        var rows = CheckedCount(count);
        if (rows > _remaining / bytesPerRow)
            throw new InvalidDataException("The landing share exceeds its decoded object and projection memory budget.");
        Reserve(rows * bytesPerRow);
    }

    private static long CheckedCount(int count)
    {
        if (count < 0) throw new InvalidDataException("The landing share has an invalid collection length.");
        return count;
    }
}

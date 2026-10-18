using System;
using System.IO;
using System.Linq;
using System.Text;
using LandingStats.App.Runtime;

namespace LandingStats.App.RegressionTests;

internal static class DurablePublicationRegressionTests
{
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "durable-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "state.json");
            var replacement = Path.Combine(root, "replacement.tmp");
            var originalBytes = Encoding.UTF8.GetBytes("original complete state");
            var replacementBytes = Encoding.UTF8.GetBytes("replacement complete state");
            File.WriteAllBytes(target, originalBytes);
            using (var stream = new FileStream(replacement, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(replacementBytes, 0, replacementBytes.Length); stream.Flush(true); }

            using (var occupiedTarget = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ExpectSharingFailure(() => DurableFile.PublishTemporary(replacement, target));
                Check(File.ReadAllBytes(target).SequenceEqual(originalBytes), "Failed replacement changed the original bytes.");
                Check(File.ReadAllBytes(replacement).SequenceEqual(replacementBytes), "Failed replacement removed its flushed bytes.");
            }

            // Exercise the observable state after a destination has disappeared.
            // This does not claim to reproduce the native ReplaceFile error 1176.
            File.Delete(target);
            using (var occupiedReplacement = new FileStream(replacement, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ExpectSharingFailure(() => DurableFile.PublishTemporary(replacement, target));
                Check(!File.Exists(target) && File.ReadAllBytes(replacement).SequenceEqual(replacementBytes),
                    "Failed promotion discarded the sole surviving replacement.");
            }
            DurableFile.PublishTemporary(replacement, target);
            Check(!File.Exists(replacement) && File.ReadAllBytes(target).SequenceEqual(replacementBytes),
                "Released replacement could not be recovered by retry.");

            var unavailableTarget = Path.Combine(root, "occupied-path");
            Directory.CreateDirectory(unavailableTarget);
            try
            {
                DurableFile.Write(unavailableTarget, stream => stream.Write(replacementBytes, 0, replacementBytes.Length));
                throw new InvalidOperationException("Publication to a directory unexpectedly succeeded.");
            }
            catch (IOException error)
            { Check((error.HResult & 0xffff) == 183, "Publication failed for a reason other than the occupied destination directory."); }
            var retained = Directory.GetFiles(root, ".w*.tmp");
            Check(retained.Length == 1 && File.ReadAllBytes(retained[0]).SequenceEqual(replacementBytes),
                "Write cleanup removed the sole flushed replacement after failed publication.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ExpectSharingFailure(Action action)
    {
        try { action(); }
        catch (IOException error)
        {
            Check((error.HResult & 0xffff) == 32, "Publication failed for a reason other than the held Windows file handle.");
            return;
        }
        throw new InvalidOperationException("Publication ignored a held Windows file handle.");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

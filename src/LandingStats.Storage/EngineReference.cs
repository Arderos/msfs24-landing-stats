using System;
using System.IO;
using System.Text;

namespace LandingStats.App.Runtime;

// A tiny independent reference survives a corrupt JSON manifest. Publish it
// before accepting source bytes so retention can still pin the exact engine.
internal static class EngineReference
{
    public static void Write(string directory, string engine)
    {
        var path = Path.Combine(directory, "engine.ref");
        if (File.Exists(path)) return;
        if (!EpisodeTransactionStore.ValidHash(engine)) throw new InvalidDataException("Invalid engine reference.");
        var bytes = Encoding.ASCII.GetBytes(engine);
        DurableFile.Write(path, output => output.Write(bytes, 0, bytes.Length));
    }
    public static string? Read(string directory)
    {
        var path = Path.Combine(directory, "engine.ref");
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        if (input.Length != 64) throw new InvalidDataException("Invalid engine reference.");
        using var reader = new StreamReader(input, Encoding.ASCII); var value = reader.ReadToEnd();
        if (!EpisodeTransactionStore.ValidHash(value)) throw new InvalidDataException("Invalid engine reference.");
        return value;
    }
}

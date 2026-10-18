namespace LandingStats.App.Runtime;

internal static class JournalCrc32
{
    private static readonly uint[] Table = BuildTable();
    public static uint Compute(byte[] bytes, int offset, int count)
    {
        var crc = uint.MaxValue;
        for (var i = offset; i < offset + count; i++) crc = Table[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
        return ~crc;
    }
    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }
}

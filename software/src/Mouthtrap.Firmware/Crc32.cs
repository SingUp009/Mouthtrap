namespace Mouthtrap.Firmware;

// CRC-32 as zlib.crc32(data, previous) computes it. ESP-IDF's NVS format passes 0xFFFFFFFF as the previous value,
// which is not the usual CRC-32 of the data, so System.IO.Hashing.Crc32 cannot be used.
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> data, uint previous)
    {
        var crc = ~previous;
        foreach (var value in data) crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }
}

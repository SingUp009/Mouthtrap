using System.Buffers.Binary;
using System.Text;

namespace Mouthtrap.Devices;

// The settings partition the Wi-Fi firmware reads: one "mouthtrap" namespace with the strings ssid, password and
// token, laid out as ESP-IDF's nvs_partition_gen (format version 2) writes them, so that it matches what
// provision.py flashes byte for byte.
public static class NvsImage
{
    public const uint Offset = 0x9000; // the nvs partition in firmware/xiao-wifi/partitions.csv
    public const int Size = 0x6000;
    public const string Namespace = "mouthtrap";

    private const int PageSize = 4096;
    private const int EntrySize = 32;
    private const int FirstEntryOffset = 64;
    private const int MaxEntries = 126;
    private const byte TypeU8 = 0x01;
    private const byte TypeString = 0x21;
    private const byte ChunkAny = 0xff;

    public static byte[] Create(WifiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var image = new byte[Size];
        image.AsSpan().Fill(0xff);
        var page = new Page(image.AsSpan(0, PageSize));
        page.WriteHeader();
        page.WriteNamespace(Namespace, 1);
        page.WriteString(1, "ssid", settings.Ssid);
        page.WriteString(1, "password", settings.Password);
        page.WriteString(1, "token", settings.Token);
        return image;
    }

    private ref struct Page(Span<byte> buffer)
    {
        private readonly Span<byte> buffer = buffer;
        private int entries;

        public readonly void WriteHeader()
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xfffffffe); // active
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], 0); // sequence number of the first page
            buffer[8] = 0xfe; // version 2
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[28..], Crc32.Compute(buffer[4..28], 0xffffffff));
        }

        public void WriteNamespace(string name, byte index)
        {
            Span<byte> entry = stackalloc byte[EntrySize];
            Header(entry, 0, TypeU8, 1, name);
            entry[24] = index;
            Seal(entry);
            Append(entry, 1);
        }

        public void WriteString(byte ns, string key, string value)
        {
            var data = Encoding.UTF8.GetBytes(value + '\0');
            var dataEntries = (data.Length + EntrySize - 1) / EntrySize;
            if (entries + 1 + dataEntries >= MaxEntries) throw new InvalidOperationException("The settings do not fit one NVS page.");
            Span<byte> entry = stackalloc byte[EntrySize];
            Header(entry, ns, TypeString, (byte)(dataEntries + 1), key);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[24..], (ushort)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], Crc32.Compute(data, 0xffffffff));
            Seal(entry);
            Append(entry, 1);
            Append(data, dataEntries);
        }

        private static void Header(Span<byte> entry, byte ns, byte type, byte span, string key)
        {
            entry.Fill(0xff);
            entry[0] = ns;
            entry[1] = type;
            entry[2] = span;
            entry[3] = ChunkAny;
            entry[8..24].Clear();
            Encoding.ASCII.GetBytes(key, entry[8..]);
        }

        // The entry CRC covers everything but itself: bytes 0-3 and 8-31.
        private static void Seal(Span<byte> entry)
        {
            Span<byte> covered = stackalloc byte[28];
            entry[..4].CopyTo(covered);
            entry[8..].CopyTo(covered[4..]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], Crc32.Compute(covered, 0xffffffff));
        }

        // Copies data into the next entries and marks each of them written (two bits per entry, 10b) in the bitmap.
        private void Append(scoped ReadOnlySpan<byte> data, int count)
        {
            data.CopyTo(buffer[(FirstEntryOffset + entries * EntrySize)..]);
            for (var i = 0; i < count; i++, entries++)
            {
                var bit = entries * 2;
                buffer[32 + bit / 8] &= (byte)~(1 << (bit % 8));
            }
        }
    }
}

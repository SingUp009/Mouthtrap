using System.IO.Compression;
using System.Text;

namespace Mouthtrap.Firmware.Tests;

// Release zip files laid out as firmware/build-release.sh writes them.
internal static class Packages
{
    public static readonly byte[] Bootloader = Image(5000);
    public static readonly byte[] PartitionTable = [0xaa, 0x50, 1, 0, 0, 0x90, 0, 0];
    public static readonly byte[] App = Image(9000);

    public const string FlashArgs = "--flash_mode dio --flash_freq 80m --flash_size 8MB\n0x0 bootloader/bootloader.bin\n" +
        "0x10000 mouthtrap_xiao_wifi.bin\n0x8000 partition_table/partition-table.bin\n";

    public static byte[] Zip(string folder = "mouthtrap-firmware-xiao-wifi-1.2.0", string flashArgs = FlashArgs,
        IEnumerable<(string Name, byte[] Data)>? files = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, $"{folder}/flash_args", Encoding.UTF8.GetBytes(flashArgs));
            foreach (var (name, data) in files ?? [("bootloader/bootloader.bin", Bootloader), ("mouthtrap_xiao_wifi.bin", App),
                ("partition_table/partition-table.bin", PartitionTable), ("LICENSE", "MIT"u8.ToArray())])
                Add(archive, $"{folder}/{name}", data);
        }
        return stream.ToArray();
    }

    public static byte[] Image(int length, ushort chip = 9)
    {
        var image = new byte[length];
        new Random(length).NextBytes(image);
        image[0] = 0xe9;
        image[12] = (byte)chip;
        image[13] = (byte)(chip >> 8);
        return image;
    }

    private static void Add(ZipArchive archive, string name, byte[] data)
    {
        using var entry = archive.CreateEntry(name).Open();
        entry.Write(data);
    }
}

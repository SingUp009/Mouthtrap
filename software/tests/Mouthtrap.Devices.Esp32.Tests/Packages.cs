using System.IO.Compression;
using System.Text;

namespace Mouthtrap.Devices.Tests;

// Release zip files laid out as firmware/build-release.sh writes them.
internal static class Packages
{
    public static readonly byte[] Bootloader = Image(5000);
    public static readonly byte[] PartitionTable = [0xaa, 0x50, 1, 0, 0, 0x90, 0, 0];
    public static readonly byte[] App = Image(9000);

    public const string FlashArgs = "--flash_mode dio --flash_freq 80m --flash_size 8MB\n0x0 bootloader/bootloader.bin\n" +
        "0x10000 mouthtrap_xiao_wifi.bin\n0x8000 partition_table/partition-table.bin\n";

    public static string Manifest(string firmware = "xiao-wifi", string version = "1.2.0", string board = "seeed-xiao-esp32s3-sense") =>
        $$"""{"format": 1, "board": "{{board}}", "firmware": "{{firmware}}", "version": "{{version}}"}""";

    // manifest null leaves manifest.json out.
    public static byte[] Zip(string folder = "mouthtrap-firmware-xiao-wifi-1.2.0", string flashArgs = FlashArgs,
        IEnumerable<(string Name, byte[] Data)>? files = null, string? manifest = "default")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, $"{folder}/flash_args", Encoding.UTF8.GetBytes(flashArgs));
            if (manifest is not null) Add(archive, $"{folder}/manifest.json", Encoding.UTF8.GetBytes(manifest == "default" ? Manifest() : manifest));
            foreach (var (name, data) in files ?? [("bootloader/bootloader.bin", Bootloader), ("mouthtrap_xiao_wifi.bin", App),
                ("partition_table/partition-table.bin", PartitionTable), ("LICENSE", "MIT"u8.ToArray())])
                Add(archive, $"{folder}/{name}", data);
        }
        return stream.ToArray();
    }

    // The USB webcam firmware of a variant, laid out as its release zip.
    public static byte[] Webcam(string firmware = "xiao-uvc", string version = "1.2.0") =>
        Zip(folder: $"mouthtrap-firmware-{firmware}-{version}",
            flashArgs: FlashArgs.Replace("mouthtrap_xiao_wifi.bin", "mouthtrap_xiao_uvc.bin", StringComparison.Ordinal),
            files: [("bootloader/bootloader.bin", Bootloader), ("mouthtrap_xiao_uvc.bin", App), ("partition_table/partition-table.bin", PartitionTable)],
            manifest: Manifest(firmware, version));

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

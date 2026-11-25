namespace Mouthtrap.Devices;

// What the ROM loader reports about an ESP32-S3 before anything is written. Sizes are null when the chip has none,
// or when the code is not one esptool knows. Nothing here tells one board design from another that uses the same chip
// and flash, such as a XIAO ESP32S3 Sense from an ESP32-S3-WROOM-1-N8R8 module.
public sealed record Esp32S3Board(string Chip, string Mac, uint FlashId, uint? FlashBytes, uint? EmbeddedPsramBytes, uint? EmbeddedFlashBytes)
{
    private const uint Megabyte = 1024 * 1024;

    // The fields as esptool 4.14 decodes them (targets/esp32s3.py and cmds.DETECTED_FLASH_SIZES). Block1 holds the
    // MAC in its first two words, then system data; block2Word4 holds the block version.
    internal static Esp32S3Board FromEfuse(uint block1Word0, uint block1Word1, uint block1Word3, uint block1Word4, uint block1Word5,
        uint block2Word4, uint flashId)
    {
        var mac = string.Join(':', new[]
        {
            (byte)(block1Word1 >> 8), (byte)block1Word1, (byte)(block1Word0 >> 24), (byte)(block1Word0 >> 16), (byte)(block1Word0 >> 8), (byte)block1Word0,
        }.Select(value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));

        var package = (block1Word3 >> 21) & 0x07;
        var rawMinor = (((block1Word5 >> 23) & 0x01) << 3) + ((block1Word3 >> 18) & 0x07);
        // The first chips (ECO0) report revision 0.0 through a block version of 1.1 and a minor version of 0.
        var eco0 = (rawMinor & 0x7) == 0 && (block2Word4 & 0x03) == 1 && ((block1Word3 >> 24) & 0x07) == 1;
        var major = eco0 ? 0 : (block1Word5 >> 24) & 0x03;
        var minor = eco0 ? 0 : rawMinor;
        var name = package switch { 0 => "ESP32-S3 (QFN56)", 1 => "ESP32-S3-PICO-1 (LGA56)", _ => "unknown ESP32-S3" };

        uint? psram = ((((block1Word5 >> 19) & 0x01) << 2) | ((block1Word4 >> 3) & 0x03)) switch
        {
            1 => 8 * Megabyte,
            2 => 2 * Megabyte,
            3 => 16 * Megabyte,
            4 => 4 * Megabyte,
            _ => null,
        };
        uint? embeddedFlash = ((block1Word3 >> 27) & 0x07) switch { 1 => 8 * Megabyte, 2 => 4 * Megabyte, _ => null };
        return new($"{name} (revision v{major}.{minor})", mac, flashId, FlashSize(flashId >> 16), psram, embeddedFlash);
    }

    private static uint? FlashSize(uint code) => code switch
    {
        0x12 or 0x32 => 256 * 1024,
        0x13 or 0x33 => 512 * 1024,
        0x14 or 0x34 => Megabyte,
        0x15 or 0x35 => 2 * Megabyte,
        0x16 or 0x36 => 4 * Megabyte,
        0x17 or 0x37 => 8 * Megabyte,
        0x18 or 0x38 => 16 * Megabyte,
        0x19 or 0x39 => 32 * Megabyte,
        0x1a or 0x20 or 0x3a => 64 * Megabyte,
        0x1b or 0x21 => 128 * Megabyte,
        0x1c or 0x22 => 256 * Megabyte,
        _ => null,
    };
}

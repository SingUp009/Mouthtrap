using Mouthtrap.Devices;

namespace Mouthtrap.Devices.Tests;

public sealed class Esp32S3BoardTests
{
    private const uint Megabyte = 1024 * 1024;

    // Expected values from esptool 4.14's own decoders (ESP32S3ROM.get_chip_description, read_mac, get_psram_cap,
    // get_flash_cap) run on the same eFuse words.
    [Theory]
    [InlineData(0u, 0u, 0u, 0u, 0u, 0u, "ESP32-S3 (QFN56) (revision v0.0)", "00:00:00:00:00:00", 0u, 0u)]
    [InlineData(0x18aabbccu, 0x3485u, 0x80000u, 0x8u, 0u, 0u, "ESP32-S3 (QFN56) (revision v0.2)", "34:85:18:aa:bb:cc", 8u, 0u)]
    [InlineData(0x1020304u, 0xa0b1u, 0x82c0000u, 0x10u, 0x1000000u, 0u, "ESP32-S3-PICO-1 (LGA56) (revision v1.3)", "a0:b1:01:02:03:04", 2u, 8u)]
    // ECO0 chips: a block version of 1.1 and a raw minor version of 0 read as v0.0 whatever the major version says.
    // PSRAM code 7 is not one esptool knows.
    [InlineData(0u, 0u, 0x1000000u, 0x18u, 0x2080000u, 1u, "ESP32-S3 (QFN56) (revision v0.0)", "00:00:00:00:00:00", 0u, 0u)]
    public void FromEfuse_Words_DecodeAsEsptoolDoes(uint word0, uint word1, uint word3, uint word4, uint word5, uint block2Word4,
        string chip, string mac, uint psramMegabytes, uint embeddedFlashMegabytes)
    {
        // When
        var board = Esp32S3Board.FromEfuse(word0, word1, word3, word4, word5, block2Word4, 0x1740c8);

        // Then
        Assert.Equal(chip, board.Chip);
        Assert.Equal(mac, board.Mac);
        Assert.Equal(psramMegabytes == 0 ? null : psramMegabytes * Megabyte, board.EmbeddedPsramBytes);
        Assert.Equal(embeddedFlashMegabytes == 0 ? null : embeddedFlashMegabytes * Megabyte, board.EmbeddedFlashBytes);
    }

    [Theory]
    [InlineData(0x1740c8u, 8u)] // GD25Q64, as in QEMU and on the XIAO
    [InlineData(0x1640efu, 4u)] // Winbond W25Q32
    [InlineData(0x3860c2u, 16u)]
    [InlineData(0x2020c2u, 64u)]
    public void FromEfuse_FlashId_GivesTheSizeEsptoolDetects(uint flashId, uint megabytes)
    {
        // When / Then
        Assert.Equal(megabytes * Megabyte, Esp32S3Board.FromEfuse(0, 0, 0, 0, 0, 0, flashId).FlashBytes);
    }

    [Theory]
    [InlineData(0xffffffu)]
    [InlineData(0x0u)]
    [InlineData(0x1140c8u)]
    public void FromEfuse_FlashIdOfUnknownSize_HasNoSize(uint flashId)
    {
        // When / Then
        Assert.Null(Esp32S3Board.FromEfuse(0, 0, 0, 0, 0, 0, flashId).FlashBytes);
    }
}

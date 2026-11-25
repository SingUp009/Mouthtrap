using Mouthtrap.Firmware;

namespace Mouthtrap.Firmware.Tests;

public sealed class SlipTests
{
    [Fact]
    public void Encode_PacketWithDelimiterAndEscape_EscapesBoth()
    {
        // When / Then
        Assert.Equal([0xc0, 0x01, 0xdb, 0xdc, 0x02, 0xdb, 0xdd, 0xc0], Slip.Encode([0x01, 0xc0, 0x02, 0xdb]));
    }

    [Fact]
    public async Task ReadAsync_FramesAmongBootMessages_ReturnsEachFrameUnescaped()
    {
        // Given text the ROM prints, an empty frame, and two frames, split across reads
        var link = new Bytes("boot:0x0 waiting for download\r\n"u8.ToArray(), [0xc0, 0xc0, 0x01, 0xdb], [0xdc, 0xc0, 0xc0, 0x02, 0xc0]);
        var reader = new SlipReader(link);

        // When
        var first = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var second = await reader.ReadAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal([0x01, 0xc0], first);
        Assert.Equal([0x02], second);
    }

    [Fact]
    public async Task ReadAsync_FrameWithAnInvalidEscape_DropsItAndReadsTheNext()
    {
        // Given
        var reader = new SlipReader(new Bytes([0xc0, 0x01, 0xdb, 0x00, 0xc0, 0xc0, 0x03, 0xc0]));

        // When / Then
        Assert.Equal([0x03], await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_LinkEndsInAFrame_ThrowsEndOfStream()
    {
        // Given
        var reader = new SlipReader(new Bytes([0xc0, 0x01]));

        // When / Then
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
    }

    // A link that returns each chunk from one read, then ends.
    private sealed class Bytes(params byte[][] chunks) : IBoardLink
    {
        private readonly Queue<byte[]> queue = new(chunks);

        public ValueTask<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            if (!queue.TryDequeue(out var chunk)) return ValueTask.FromResult(0);
            chunk.CopyTo(buffer, 0);
            return ValueTask.FromResult(chunk.Length);
        }

        public ValueTask WriteAsync(byte[] data, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void DiscardInput() { }
        public ValueTask<bool> EnterDownloadModeAsync(int attempt, TimeProvider clock, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> RestartAsync(TimeProvider clock, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public void Dispose() { }
    }
}

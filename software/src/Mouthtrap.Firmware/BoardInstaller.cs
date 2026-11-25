using System.Text;
using System.Text.RegularExpressions;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Firmware;

public enum FlashStage { Connecting, Preparing, Writing, Restarting, WaitingForCamera }

// How far an installation is. Written and Total count the bytes of all parts.
public sealed record FlashProgress(FlashStage Stage, long Written, long Total, string? Part = null);

// The firmware started, but the camera did not report its URL: the firmware logged an error (FirmwareFailed), or
// nothing useful arrived in time (CameraUrlNotSeen). The firmware is written either way.
public sealed record FirmwareFailed(string Message);
public sealed record CameraUrlNotSeen(string Message);

[Error]
public partial union StartupError(FirmwareFailed, CameraUrlNotSeen);

// Restarted is false when the link could not restart the board, which then needs a press of RESET. Camera is the
// result of watching the Wi-Fi firmware start, or null when it was not watched.
public sealed record InstallReport(bool Restarted, Result<StreamUrl, StartupError>? Camera);

public static partial class BoardInstaller
{
    public static readonly TimeSpan CameraTimeout = TimeSpan.FromSeconds(60);

    // Writes plan to the board, verifies every part and restarts the board. With watchCamera, it then reads the Wi-Fi
    // firmware's log until the camera URL appears. The board link is owned by this run and released however it ends.
    public static Effect<InstallReport, FlashError, FirmwareEnvironment> Install(Resource<IBoardLink, FlashError, FirmwareEnvironment> board,
        FlashPlan plan, bool watchCamera = false, IProgress<FlashProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Scope<FlashError, FirmwareEnvironment>.Gen(async scope =>
        {
            var clock = scope.Env.Resolve<TimeProvider>();
            var total = plan.TotalBytes;
            progress?.Report(new(FlashStage.Connecting, 0, total));
            var link = await scope.Acquire(board);
            var loader = new RomLoader(link, clock);
            scope.Get(await loader.ConnectAsync(scope.CancellationToken));
            progress?.Report(new(FlashStage.Preparing, 0, total));
            scope.Get(await loader.PrepareAsync(plan.FlashSize, scope.CancellationToken));
            long done = 0;
            foreach (var part in plan.Parts)
            {
                var before = done;
                progress?.Report(new(FlashStage.Writing, before, total, part.Name));
                scope.Get(await loader.WriteAsync(part, written => progress?.Report(new(FlashStage.Writing, before + written, total, part.Name)),
                    scope.CancellationToken));
                done += part.Data.Length;
            }
            progress?.Report(new(FlashStage.Restarting, total, total));
            var restarted = await loader.RestartAsync(scope.CancellationToken);
            if (!watchCamera || !restarted) return new InstallReport(restarted, null);
            progress?.Report(new(FlashStage.WaitingForCamera, total, total));
            var camera = await WaitForCameraAsync(link, clock, scope.CancellationToken);
            return new InstallReport(restarted, camera);
        }).OnThreadPool();
    }

    // Reads the firmware's log (UTF-8 lines, possibly with ANSI colours) until it reports the camera URL or an error.
    internal static async Task<Result<StreamUrl, StartupError>> WaitForCameraAsync(IBoardLink link, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(CameraTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var buffer = new byte[1024];
        var text = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            while (true)
            {
                var read = await link.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (read == 0) return NotSeen("The board's log ended before the camera started.");
                text.Append(chars, 0, decoder.GetChars(buffer, 0, read, chars, 0));
                for (var end = IndexOfNewLine(text); end >= 0; end = IndexOfNewLine(text))
                {
                    var line = AnsiColour().Replace(text.ToString(0, end), "").Trim();
                    text.Remove(0, end + 1);
                    if (ReadLine(line) is { } result) return result;
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return NotSeen($"The board did not report the camera URL within {CameraTimeout.TotalSeconds:0} seconds.");
        }
        catch (IOException exception)
        {
            return NotSeen($"The board's log could not be read after the restart: {exception.Message}");
        }
    }

    // The URL from "Camera URL: http://<ip>/stream", or the message of an error the firmware logged, such as a Wi-Fi
    // network it could not join; null for any other line.
    internal static Result<StreamUrl, StartupError>? ReadLine(string line)
    {
        if (CameraUrlLine().Match(line) is { Success: true } camera)
            return StreamUrl.Create(camera.Groups[1].Value).TryGetOk(out var url)
                ? new Ok<StreamUrl>(url)
                : new Err<StartupError>(new FirmwareFailed($"The board reported a stream URL that cannot be used: {camera.Groups[1].Value}"));
        if (ErrorLine().Match(line) is { Success: true } error) return new Err<StartupError>(new FirmwareFailed(error.Groups[1].Value));
        return null;
    }

    private static int IndexOfNewLine(StringBuilder text)
    {
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') return i;
        return -1;
    }

    private static Err<StartupError> NotSeen(string message) => new(new CameraUrlNotSeen(message));

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiColour();

    [GeneratedRegex(@"Camera URL: (\S+)")]
    private static partial Regex CameraUrlLine();

    [GeneratedRegex(@"^E \(\d+\) mouthtrap: (.+)$")]
    private static partial Regex ErrorLine();
}

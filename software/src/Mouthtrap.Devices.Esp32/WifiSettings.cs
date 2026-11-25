using System.Text;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Devices;

// What the Wi-Fi firmware reads from its nvs partition when it starts, checked with the firmware's own rules
// (settings_validate in firmware/xiao-wifi/components/mouthtrap_stream), so the board never receives settings it
// would refuse. The password and the token are never in a message or in ToString.
public sealed record WifiSettings
{
    public const int MaxSsidBytes = 32;

    private WifiSettings(string ssid, string password, string token)
    {
        Ssid = ssid;
        Password = password;
        Token = token;
    }

    public string Ssid { get; }
    public string Password { get; }
    public string Token { get; }

    public static Result<WifiSettings, InvalidValue> Create(string? ssid, string? password, string? token)
    {
        ssid ??= "";
        password ??= "";
        token ??= "";
        if (!InRange(ssid, 1, MaxSsidBytes))
            return Invalid(nameof(Ssid), "The network name must be 1-32 bytes of text.");
        if (!InRange(password, 8, 63))
            return Invalid(nameof(Password), "The Wi-Fi password must be 8-63 bytes of text.");
        if (!StreamToken.IsValid(token))
            return Invalid(nameof(Token), "The stream token must be 32-120 letters, digits, '-' or '_'.");
        return new Ok<WifiSettings>(new(ssid, password, token));
    }

    public override string ToString() => $"WifiSettings {{ Ssid = {Ssid} }}";

    // The firmware counts UTF-8 bytes. Control characters cannot be typed into a network name and would only hide
    // what is stored.
    private static bool InRange(string text, int minimum, int maximum) =>
        !text.Any(char.IsControl) && Encoding.UTF8.GetByteCount(text) is var length && length >= minimum && length <= maximum;

    private static Err<InvalidValue> Invalid(string name, string message) => new(new(name, message));
}

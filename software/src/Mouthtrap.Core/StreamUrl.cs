using Moderato.Results;

namespace Mouthtrap.Core;

// An absolute HTTP(S) URL of an MJPEG stream. Credentials belong in the token file, not in a URL that is saved in
// settings and shown on screen, so a user name or password is refused.
public sealed record StreamUrl
{
    private StreamUrl(Uri address) => Address = address;

    public Uri Address { get; }

    public static Result<StreamUrl, InvalidValue> Create(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            return new Err<InvalidValue>(new(nameof(StreamUrl), "The stream URL must be an absolute HTTP(S) URL."));
        if (address.UserInfo.Length > 0)
            return new Err<InvalidValue>(new(nameof(StreamUrl), "The stream URL must not contain a user name or password."));
        return new Ok<StreamUrl>(new(address));
    }

    public override string ToString() => Address.ToString();
}

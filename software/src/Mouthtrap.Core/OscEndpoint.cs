using System.Net;
using Moderato.Results;

namespace Mouthtrap.Core;

// Where VRChat listens for OSC: an IP literal with an explicit, nonzero port, so nothing is resolved by name.
public sealed record OscEndpoint
{
    private OscEndpoint(IPEndPoint address) => Address = address;

    public IPEndPoint Address { get; }

    public static Result<OscEndpoint, InvalidValue> Create(string? text) =>
        text is not null && text.Contains(':', StringComparison.Ordinal) && IPEndPoint.TryParse(text, out var parsed) && parsed.Port != 0
            ? new Ok<OscEndpoint>(new(parsed))
            : new Err<InvalidValue>(new(nameof(OscEndpoint), "The OSC target must be an IP address and port, such as 127.0.0.1:9000."));

    public override string ToString() => Address.ToString();
}

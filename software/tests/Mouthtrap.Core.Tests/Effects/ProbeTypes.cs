using Moderato.Effects;
using Moderato.Results;

namespace Mouthtrap.Core.Tests.Effects;

public sealed record DeviceMissing(string Id);
public sealed record InvalidSettings(string Field);

[Error]
public partial union ProbeError(DeviceMissing, InvalidSettings);

[Moderato.Effects.Environment]
public sealed partial record ProbeEnvironment(ProbeDevices Devices);

public sealed class ProbeDevices
{
    public List<string> Events { get; } = [];

    public Resource<string, ProbeError, R> Open<R>(string name) where R : IHas<ProbeDevices> =>
        Resource.Make(
            Effect<R>.Access(env =>
            {
                env.Resolve<ProbeDevices>().Events.Add($"open:{name}");
                return name;
            }),
            (device, _) => Effect<R>.Access(env =>
            {
                env.Resolve<ProbeDevices>().Events.Add($"close:{device}");
                return Unit.Value;
            }));
}

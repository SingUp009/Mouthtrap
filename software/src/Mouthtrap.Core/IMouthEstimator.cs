using System.Collections.Immutable;
using Moderato.Effects;
using Moderato.Results;

namespace Mouthtrap.Core;

public sealed record ModelFileMissing(string Path);
public sealed record ModelLoadFailed(string Message);
public sealed record ModelContractMismatch(string Message);
public sealed record ModelInferenceFailed(string Message);

[Error]
public partial union ModelError(ModelFileMissing, ModelLoadFailed, ModelContractMismatch, ModelInferenceFailed);

public readonly record struct MouthExpression(string Name, float Raw)
{
    public float Raw { get; } = float.IsFinite(Raw) ? Raw : throw new ArgumentOutOfRangeException(nameof(Raw));
    public float Value => Math.Clamp(Raw, 0f, 1f);
}

public sealed record MouthEstimate
{
    public float RawJawOpen { get; }
    public float JawOpen => Math.Clamp(RawJawOpen, 0f, 1f);
    public ImmutableArray<MouthExpression> AdditionalExpressions { get; }

    public MouthEstimate(float rawJawOpen, ImmutableArray<MouthExpression> additionalExpressions = default)
    {
        if (!float.IsFinite(rawJawOpen)) throw new ArgumentOutOfRangeException(nameof(rawJawOpen));
        RawJawOpen = rawJawOpen;
        AdditionalExpressions = additionalExpressions.IsDefault ? [] : additionalExpressions;
    }
}

public interface IMouthEstimator : IDisposable
{
    int InputWidth { get; }
    int InputHeight { get; }
    // Packed RGB24 at InputWidth × InputHeight; calls are serial and finish before Dispose.
    Result<MouthEstimate, ModelInferenceFailed> Estimate(ReadOnlySpan<byte> rgb24, CancellationToken cancellationToken);
}

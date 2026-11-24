using Moderato.Results;

namespace Mouthtrap.Core;

// Values at or below this, after calibration and smoothing, are sent as 0 and the rest is stretched back to 0..1,
// so small resting residuals do not leave the mouth slightly open. It is at least 0 and below 0.5. None (the default
// value of the type) sends values as they are.
public readonly record struct Deadzone
{
    // Chosen on two recorded sessions: 0.15 took resting residuals from about 0.15 to 0.01 while talking kept its
    // movement and smiles and pucker lost about a tenth.
    public const float DefaultValue = .15f;

    private Deadzone(float value) => Value = value;

    public float Value { get; }
    public static Deadzone None => default;
    public static Deadzone Default { get; } = new(DefaultValue);

    public static Result<Deadzone, InvalidValue> Create(float value) =>
        float.IsFinite(value) && value is >= 0f and < .5f ? new Ok<Deadzone>(new(value))
            : new Err<InvalidValue>(new(nameof(Deadzone), "The deadzone must be at least 0 and below 0.5."));

    public float Apply(float value) => Value == 0f ? value : Math.Max(0f, (value - Value) / (1f - Value));
}

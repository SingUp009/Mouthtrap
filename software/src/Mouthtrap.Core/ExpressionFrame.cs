namespace Mouthtrap.Core;

// Unified Expressions coefficients in 0..1. A key the source does not provide is null, which is not the same
// as a provided 0; only an output format that cannot tell them apart turns it into 0.
public sealed class ExpressionFrame
{
    public static int KeyCount { get; } = Enum.GetValues<UnifiedExpression>().Length;
    public static ExpressionFrame Empty { get; } = new(new float?[KeyCount]);

    private readonly float?[] values;

    private ExpressionFrame(float?[] values) => this.values = values;

    public float? this[UnifiedExpression key] => values[IndexOf(key)];

    public bool IsProvided(UnifiedExpression key) => this[key].HasValue;

    public int ProvidedCount => values.Count(value => value.HasValue);

    // Provided keys in enum order.
    public IEnumerable<KeyValuePair<UnifiedExpression, float>> Provided
    {
        get
        {
            for (var i = 0; i < values.Length; i++)
                if (values[i] is { } value) yield return new((UnifiedExpression)i, value);
        }
    }

    public static ExpressionFrame Create(IEnumerable<KeyValuePair<UnifiedExpression, float>> provided)
    {
        ArgumentNullException.ThrowIfNull(provided);
        var values = new float?[KeyCount];
        foreach (var (key, value) in provided)
        {
            var index = IndexOf(key);
            if (!float.IsFinite(value) || value is < 0f or > 1f)
                throw new ArgumentOutOfRangeException(nameof(provided), $"{key} must be within 0..1.");
            if (values[index].HasValue) throw new ArgumentException($"{key} is provided more than once.", nameof(provided));
            values[index] = value;
        }
        return new(values);
    }

    private static int IndexOf(UnifiedExpression key) =>
        Enum.IsDefined(key) ? (int)key : throw new ArgumentOutOfRangeException(nameof(key));
}

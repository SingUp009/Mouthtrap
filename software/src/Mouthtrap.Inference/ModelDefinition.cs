using System.Text.Json.Serialization;
using Mouthtrap.Core;

namespace Mouthtrap.Inference;

public enum TensorLayout { Nchw, Nhwc }
public enum InputColor { Gray, Rgb, Bgr }

public sealed record ModelDefinition
{
    public required string ModelPath { get; init; }
    public required string InputName { get; init; }
    public required string OutputName { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required TensorLayout Layout { get; init; }
    public required InputColor Color { get; init; }
    public required int OutputCount { get; init; }
    public required int JawOpenIndex { get; init; }
    public Dictionary<string, int>? ExpressionIndices { get; init; }
    public float InputScale { get; init; } = 1f / 255f;
    public float InputBias { get; init; }
    public string? Sha256 { get; init; }
    [JsonIgnore] public int Channels => Color == InputColor.Gray ? 1 : 3;
    [JsonIgnore] public long[] Shape => Layout == TensorLayout.Nchw ? [1, Channels, Height, Width] : [1, Height, Width, Channels];

    // The additional outputs by shape. Only for a definition that passed Validate, which rejects unknown names.
    public KeyValuePair<ArkitShape, int>[] Expressions() =>
        ExpressionIndices?.Select(item => ArkitExpressionMap.TryParse(item.Key, out var shape)
            ? new KeyValuePair<ArkitShape, int>(shape, item.Value)
            : throw new InvalidOperationException($"'{item.Key}' was not validated.")).ToArray() ?? [];

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelPath) || string.IsNullOrWhiteSpace(InputName) || string.IsNullOrWhiteSpace(OutputName))
            return ModelMessages.Get("ModelPathRequired");
        if (Width <= 0 || Height <= 0 || Width > 4096 || Height > 4096)
            return ModelMessages.Get("InputSizeOutOfRange");
        if (!Enum.IsDefined(Layout) || !Enum.IsDefined(Color)) return ModelMessages.Get("TensorFormatUnsupported");
        if (OutputCount <= 0 || OutputCount > 65536 || JawOpenIndex < 0 || JawOpenIndex >= OutputCount)
            return ModelMessages.Get("OutputIndexOutOfRange");
        if (ExpressionIndices is { } expressions)
        {
            if (expressions.Count > 128) return ModelMessages.Get("TooManyExpressions");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { nameof(ArkitShape.JawOpen) };
            var indices = new HashSet<int> { JawOpenIndex };
            foreach (var (name, index) in expressions)
            {
                if (name.Length is < 1 or > 64 || !char.IsAsciiLetter(name[0]) ||
                    name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || !names.Add(name))
                    return ModelMessages.Format("ExpressionNameInvalid", name);
                if (!ArkitExpressionMap.TryParse(name, out _))
                    return ModelMessages.Format("ExpressionNameUnmapped", name);
                if (index < 0 || index >= OutputCount || !indices.Add(index))
                    return ModelMessages.Format("ExpressionIndexInvalid", name);
            }
        }
        if (!float.IsFinite(InputScale) || !float.IsFinite(InputBias) || InputScale == 0 ||
            !float.IsFinite(255 * InputScale + InputBias)) return ModelMessages.Get("InputScaleInvalid");
        if (Sha256 is not null && (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)))
            return ModelMessages.Get("Sha256Invalid");
        return null;
    }
}

public static class OnnxInputEncoder
{
    public static void Encode(ReadOnlySpan<byte> rgb24, Span<float> input, ModelDefinition definition)
    {
        var pixels = checked(definition.Width * definition.Height);
        if (rgb24.Length != (long)pixels * 3 || input.Length != (long)pixels * definition.Channels)
            throw new ArgumentException("RGB24 input or tensor buffer has an unexpected size.");
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            var at = pixel * 3;
            if (definition.Color == InputColor.Gray)
            {
                var gray = MathF.Round(0.299f * rgb24[at] + 0.587f * rgb24[at + 1] + 0.114f * rgb24[at + 2]);
                input[pixel] = gray * definition.InputScale + definition.InputBias;
            }
            else
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var source = definition.Color == InputColor.Bgr ? 2 - channel : channel;
                    var index = definition.Layout == TensorLayout.Nchw ? channel * pixels + pixel : pixel * 3 + channel;
                    input[index] = rgb24[at + source] * definition.InputScale + definition.InputBias;
                }
            }
        }
    }
}

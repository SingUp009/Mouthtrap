using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Inference.Tests;

public sealed class OnnxEstimatorTests
{
    // Synthetic ReduceMean graph with no trained weights, using the ONNX protobuf schema.
    private const string MeanModel = "CAgSD01vdXRodHJhcCB0ZXN0czprCikKBWltYWdlEgNqYXciClJlZHVjZU1lYW4qDwoIa2VlcGRpbXMYAKABAhIOc3ludGhldGljX21lYW5aHwoFaW1hZ2USFgoUCAESEAoCCAEKAggDCgIIAQoCCAJiDQoDamF3EgYKBAgBEgBCAhAN";
    // A synthetic image/image graph lets a zero generate NaN; TestModels.Identity preserves tensor order.
    private const string DivideModel = "CAg6ZQoYCgVpbWFnZQoFaW1hZ2USA2phdyIDRGl2EglzeW50aGV0aWNaHwoFaW1hZ2USFgoUCAESEAoCCAEKAggDCgIIAQoCCAJiHQoDamF3EhYKFAgBEhAKAggBCgIIAwoCCAEKAggCQgIQDQ==";

    private static ModelDefinition Definition => new()
    {
        ModelPath = "mean.onnx",
        InputName = "image",
        OutputName = "jaw",
        Width = 2,
        Height = 1,
        Color = InputColor.Rgb,
        Layout = TensorLayout.Nchw,
        OutputCount = 1,
        JawOpenIndex = 0
    };

    [Theory]
    [InlineData(InputColor.Rgb, TensorLayout.Nchw, new float[] { 1, 4, 2, 5, 3, 6 })]
    [InlineData(InputColor.Bgr, TensorLayout.Nchw, new float[] { 3, 6, 2, 5, 1, 4 })]
    [InlineData(InputColor.Rgb, TensorLayout.Nhwc, new float[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(InputColor.Bgr, TensorLayout.Nhwc, new float[] { 3, 2, 1, 6, 5, 4 })]
    public void Encode_ColorAndLayout_WritesChannelsInDeclaredOrderAndNormalizes(InputColor color, TensorLayout layout, float[] expected)
    {
        // Given
        var input = new float[6];

        // When
        OnnxInputEncoder.Encode([1, 2, 3, 4, 5, 6], input, Definition with { Color = color, Layout = layout, InputScale = 2, InputBias = -1 });

        // Then
        Assert.Equal(expected.Select(value => value * 2 - 1).ToArray(), input);
    }

    [Fact]
    public void Encode_Gray_WritesRoundedBt601InOneChannel()
    {
        // Given
        var input = new float[2];
        var definition = Definition with { Color = InputColor.Gray };

        // When
        OnnxInputEncoder.Encode([255, 0, 0, 0, 255, 0], input, definition);

        // Then
        Assert.Equal(76f / 255, input[0], 6);
        Assert.Equal(150f / 255, input[1], 6);
        Assert.Equal(new long[] { 1, 1, 1, 2 }, definition.Shape);
        Assert.Throws<ArgumentException>(() => OnnxInputEncoder.Encode([1], input, definition));
    }

    [Fact]
    public void Validate_InvalidDefinitionValue_ReturnsMessage()
    {
        // Given
        var valid = Definition;
        Assert.Null(valid.Validate());
        ModelDefinition[] invalid = [valid with { ModelPath = "" }, valid with { InputName = "" }, valid with { Width = 0 },
            valid with { Height = 4097 }, valid with { Layout = (TensorLayout)99 }, valid with { Color = (InputColor)99 },
            valid with { OutputCount = 0 }, valid with { JawOpenIndex = 1 }, valid with { InputScale = float.NaN },
            valid with { InputBias = float.PositiveInfinity }, valid with { Sha256 = "wrong" }];

        // When / Then
        Assert.All(invalid, definition => Assert.NotNull(definition.Validate()));
    }

    [Fact]
    public async Task Load_ModelWithoutHash_RunsCpuInferenceAndReleasesFile()
    {
        // Given
        using var files = new Fixture();

        // When
        var exit = await OnnxMouthEstimator.Load(files.Config).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Ok<IMouthEstimator>);
        var model = ((Ok<IMouthEstimator>)exit.Value).Value;
        using (model)
        {
            // When / Then: a real CPU inference, and a cancelled one.
            var result = model.Estimate([0, 0, 0, 255, 255, 255], CancellationToken.None);
            Assert.True(result is Ok<MouthEstimate>);
            var estimate = ((Ok<MouthEstimate>)result.Value).Value;
            Assert.Equal(0.5f, estimate.JawOpen, 5);
            Assert.Empty(estimate.AdditionalExpressions);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => model.Estimate(new byte[6], cancellation.Token));
        }

        // Then: the disposed model refuses work and no longer holds the file.
        Assert.Throws<ObjectDisposedException>(() => model.Estimate(new byte[6], CancellationToken.None));
        using (File.Open(files.Model, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Theory]
    [InlineData("MouthSmileLeft", 6)]
    [InlineData("Mouth Smile", 1)]
    [InlineData("JawOpen", 1)]
    [InlineData("MouthSmileLeft", 0)]
    [InlineData("MouthCornerPullLeft", 1)]
    [InlineData("mouthSmileLeft", 1)]
    public void Validate_InvalidExpressionNameOrIndex_ReturnsMessage(string name, int index)
    {
        // Given
        var definition = Definition with { OutputCount = 6, ExpressionIndices = new() { [name] = index } };

        // When
        var message = definition.Validate();

        // Then
        Assert.NotNull(message);
    }

    [Fact]
    public void Validate_ShippedBabbleExample_NamesOnlyMappedExpressions()
    {
        // Given
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "Mouthtrap.slnx"))) directory = directory.Parent!;
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var example = JsonSerializer.Deserialize<ModelDefinition>(
            File.ReadAllText(Path.Combine(directory.FullName, "examples", "models", "babble45.json")), options)!;

        // When / Then
        Assert.Null(example.Validate());
        Assert.Equal(44, example.ExpressionIndices!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Estimate_MappedOutputs_KeepsOrderAndReportsNonFiniteAsTypedFailure(bool nonfinite)
    {
        // Given
        using var files = new Fixture();
        File.WriteAllBytes(files.Model, Convert.FromBase64String(nonfinite ? DivideModel : TestModels.Identity));
        files.WriteDefinition(Definition with
        {
            OutputCount = 6,
            InputScale = .01f,
            InputBias = -.5f,
            ExpressionIndices = new() { ["MouthSmileLeft"] = 4, ["MouthSmileRight"] = 1, ["MouthPucker"] = 2 }
        });
        var exit = await OnnxMouthEstimator.Load(files.Config).RunExitAsync(new(TimeProvider.System), CancellationToken.None);
        using var model = Assert.IsType<Ok<IMouthEstimator>>(exit.Value).Value;

        // When
        var result = model.Estimate([100, 75, nonfinite ? (byte)50 : (byte)25, 175, 150, 25], CancellationToken.None);

        // Then
        if (nonfinite)
        {
            Assert.True(result is Err<ModelInferenceFailed>(var failure) && failure.Message.Contains("MouthSmileLeft", StringComparison.Ordinal));
            return;
        }
        var estimate = Assert.IsType<Ok<MouthEstimate>>(result.Value).Value;
        Assert.Equal(.5f, estimate.RawJawOpen, 5);
        Assert.Collection(estimate.AdditionalExpressions, left =>
        {
            Assert.Equal(ArkitShape.MouthSmileLeft, left.Shape);
            Assert.Equal(-.25f, left.Raw, 5);
            Assert.Equal(0f, left.Value);
        }, right =>
        {
            Assert.Equal(ArkitShape.MouthSmileRight, right.Shape);
            Assert.Equal(1.25f, right.Raw, 5);
            Assert.Equal(1f, right.Value);
        }, pucker => Assert.Equal(.25f, pucker.Value, 5));
        _ = model.Estimate([100, 100, 100, 100, 100, 100], CancellationToken.None);
        Assert.Equal(-.25f, estimate.AdditionalExpressions[0].Raw, 5);
    }

    [Fact]
    public async Task Load_RepeatedJsonMappingKey_FailsWithContractMismatch()
    {
        // Given
        using var files = new Fixture();
        File.WriteAllBytes(files.Model, Convert.FromBase64String(TestModels.Identity));
        files.WriteDefinition(Definition with { OutputCount = 6 });
        var original = File.ReadAllText(files.Config);
        // Replace the serialized null to test duplicate expression names inside a single object.
        File.WriteAllText(files.Config, original.Replace("\"ExpressionIndices\":null", "\"ExpressionIndices\":{\"MouthSmileLeft\":1,\"MouthSmileLeft\":2}"));

        // When
        var exit = await OnnxMouthEstimator.Load(files.Config).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<ModelError>(ModelContractMismatch));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("input-name")]
    [InlineData("shape")]
    [InlineData("output-count")]
    [InlineData("corrupt-model")]
    [InlineData("missing-model")]
    [InlineData("invalid-json")]
    [InlineData("missing-config")]
    public async Task Load_BadModelOrDefinition_FailsWithTypedErrorAndReleasesFiles(string condition)
    {
        // Given
        using var files = new Fixture();
        var definition = Definition;
        switch (condition)
        {
            case "hash": definition = definition with { Sha256 = new string('0', 64) }; break;
            case "input-name": definition = definition with { InputName = "wrong" }; break;
            case "shape": definition = definition with { Width = 3 }; break;
            case "output-count": definition = definition with { OutputCount = 2 }; break;
        }
        files.WriteDefinition(definition);
        if (condition == "corrupt-model") File.WriteAllText(files.Model, "invalid ONNX");
        if (condition == "missing-model") File.Delete(files.Model);
        if (condition == "invalid-json") File.WriteAllText(files.Config, "{}");
        if (condition == "missing-config") File.Delete(files.Config);

        // When
        var exit = await OnnxMouthEstimator.Load(files.Config).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<ModelError>, exit.ToString());
        if (File.Exists(files.Model)) using (File.Open(files.Model, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Theory]
    [InlineData("hash", "contract")]
    [InlineData("input-name", "contract")]
    [InlineData("shape", "contract")]
    [InlineData("output-count", "contract")]
    [InlineData("invalid-json", "contract")]
    [InlineData("corrupt-model", "load")]
    [InlineData("model-is-folder", "load")]
    [InlineData("missing-model", "missing")]
    [InlineData("missing-config", "missing")]
    public async Task Load_BadModelOrDefinition_ReportsTheMatchingCase(string condition, string expected)
    {
        // Given
        using var files = new Fixture();
        var definition = condition switch
        {
            "hash" => Definition with { Sha256 = new string('0', 64) },
            "input-name" => Definition with { InputName = "wrong" },
            "shape" => Definition with { Width = 3 },
            "output-count" => Definition with { OutputCount = 2 },
            _ => Definition
        };
        files.WriteDefinition(definition);
        if (condition == "corrupt-model") File.WriteAllText(files.Model, "invalid ONNX");
        if (condition is "missing-model" or "model-is-folder") File.Delete(files.Model);
        if (condition == "model-is-folder") Directory.CreateDirectory(files.Model);
        if (condition == "invalid-json") File.WriteAllText(files.Config, "{}");
        if (condition == "missing-config") File.Delete(files.Config);

        // When
        var exit = await OnnxMouthEstimator.Load(files.Config).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        var actual = exit switch
        {
            Err<ModelError>(ModelContractMismatch) => "contract",
            Err<ModelError>(ModelLoadFailed) => "load",
            Err<ModelError>(ModelFileMissing) => "missing",
            _ => exit.ToString()
        };
        Assert.Equal(expected, actual);
        if (condition == "model-is-folder") Directory.Delete(files.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_FileMissing_NamesTheFullPathOfThatFile(bool definition)
    {
        // Given
        using var files = new Fixture();
        files.WriteDefinition(Definition);
        var missing = definition ? files.Config : files.Model;
        File.Delete(missing);

        // When
        var exit = await OnnxMouthEstimator.Load(files.Config).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<ModelError>(ModelFileMissing(var path)) && path == Path.GetFullPath(missing), exit.ToString());
    }

    [Fact]
    public async Task Load_Cancelled_IsInterruptedNotAFailure()
    {
        // Given
        using var files = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // When
        var exit = await OnnxMouthEstimator.Load(files.Config).RunExitAsync(new(TimeProvider.System), cancellation.Token);

        // Then
        Assert.True(exit is Interrupted, exit.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), $"mouthtrap-model-{Guid.NewGuid():N}");
        public string Model => Path.Combine(folder, "mean.onnx");
        public string Config => Path.Combine(folder, "model.json");
        public Fixture()
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Model, Convert.FromBase64String(MeanModel));
            WriteDefinition(Definition);
        }
        public void WriteDefinition(ModelDefinition definition)
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            File.WriteAllText(Config, JsonSerializer.Serialize(definition, options));
        }
        public void Dispose()
        {
            File.Delete(Config);
            File.Delete(Model);
            Directory.Delete(folder);
        }
    }

    [Theory]
    [InlineData("en", "The expression name 'NotAFace' is not in the mapping table. Use an ARKit-style name such as MouthSmileLeft.")]
    [InlineData("ja", "表情名 'NotAFace' は対応表にありません。ARKit風の名前（例: MouthSmileLeft）で指定してください。")]
    public void Validate_InvalidExpressionName_ReturnsMessageInUiLanguage(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            // Given
            var definition = Definition with { OutputCount = 6, ExpressionIndices = new() { ["NotAFace"] = 1 } };

            // When
            var message = definition.Validate();

            // Then
            Assert.Equal(expected, message);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void ModelMessages_EveryMessage_HasJapaneseTranslation()
    {
        // Given
        var resources = new ResourceManager("Mouthtrap.Inference.ModelMessages", typeof(ModelDefinition).Assembly);

        // When / Then
        ResourceAssert.Translated(resources, "ja");
    }
}

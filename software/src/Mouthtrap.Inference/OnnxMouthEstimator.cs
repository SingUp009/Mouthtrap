using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML.OnnxRuntime;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Inference;

public sealed class OnnxMouthEstimator : IMouthEstimator
{
    private readonly ModelDefinition definition;
    public int InputWidth => definition.Width;
    public int InputHeight => definition.Height;
    private readonly InferenceSession session;
    private readonly float[] input;
    private readonly OrtValue inputValue;
    private readonly Dictionary<string, OrtValue> inputs;
    private readonly KeyValuePair<ArkitShape, int>[] expressionIndices;
    private bool disposed;

    private OnnxMouthEstimator(InferenceSession session, ModelDefinition definition)
    {
        this.definition = definition;
        this.session = session;
        expressionIndices = definition.Expressions();
        input = new float[checked(definition.Width * definition.Height * definition.Channels)];
        inputValue = OrtValue.CreateTensorValueFromMemory(input, definition.Shape);
        try { inputs = new() { [definition.InputName] = inputValue }; }
        catch { inputValue.Dispose(); throw; }
    }

    // Expected failures are typed: a missing file, a file that cannot be read, a definition or model that does not fit
    // the contract, and a runtime that cannot load the model. Caller cancellation is Interrupted; anything else is a
    // defect.
    public static Effect<IMouthEstimator, ModelError, TrackingEnvironment> Load(string definitionPath) =>
        Scope<ModelError, TrackingEnvironment>.Gen(async scope =>
        {
            scope.CancellationToken.ThrowIfCancellationRequested();
            var (config, fullPath) = await scope.Run(Effect<TrackingEnvironment>.FromResultAsync(cancellationToken =>
                ReadFile(definitionPath, null, 65536, cancellationToken)));
            if (config is null) throw scope.Fail(new ModelContractMismatch(ModelMessages.Get("DefinitionTooLarge")));
            var definition = await scope.Run(Effect<TrackingEnvironment>.Try<JsonException, ModelDefinition?, ModelError>(
                    () => JsonSerializer.Deserialize<ModelDefinition>(config, JsonOptions),
                    exception => new ModelContractMismatch(exception.Message)))
                ?? throw scope.Fail(new ModelContractMismatch(ModelMessages.Get("DefinitionEmpty")));
            if (definition.Validate() is { } invalid) throw scope.Fail(new ModelContractMismatch(invalid));
            var (data, _) = await scope.Run(Effect<TrackingEnvironment>.FromResultAsync(cancellationToken =>
                ReadFile(definition.ModelPath, Path.GetDirectoryName(fullPath)!, 512L * 1024 * 1024, cancellationToken)));
            if (data is null) throw scope.Fail(new ModelContractMismatch(ModelMessages.Get("ModelTooLarge")));
            if (definition.Sha256 is { } expected && !Convert.ToHexString(SHA256.HashData(data)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw scope.Fail(new ModelContractMismatch(ModelMessages.Get("Sha256Mismatch")));
            scope.CancellationToken.ThrowIfCancellationRequested();
            return scope.Get(Create(data, definition));
        }).OnThreadPool();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    // Reads a definition or model file whole, or returns null data when it is larger than the limit. The exceptions
    // classified here are the ones resolving, opening and reading a file raise: a missing file or folder, and a file
    // that cannot be read (access denied, in use, invalid path). Cancellation and anything else propagate.
    private static async ValueTask<Result<(byte[]? Data, string FullPath), ModelError>> ReadFile(string path, string? relativeTo,
        long limit, CancellationToken cancellationToken)
    {
        string? fullPath = null;
        try
        {
            fullPath = relativeTo is null ? Path.GetFullPath(path) : Path.GetFullPath(path, relativeTo);
            await using var file = File.OpenRead(fullPath);
            if (file.Length > limit) return new Ok<(byte[]?, string)>((null, fullPath));
            var data = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            return new Ok<(byte[]?, string)>((data, fullPath));
        }
        // Only opening the resolved path reports a missing file or folder, so fullPath is set by then.
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException && fullPath is not null)
        { return new Err<ModelError>(new ModelFileMissing(fullPath)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new Err<ModelError>(new ModelLoadFailed(exception.Message)); }
    }

    // Creates the session and checks it against the definition. ONNX Runtime reports a model it cannot load, or a
    // missing native library, with the exceptions classified here; a size that overflows does not fit the definition.
    // The session is disposed unless the estimator takes it.
    private static Result<IMouthEstimator, ModelError> Create(byte[] data, ModelDefinition definition)
    {
        InferenceSession? session = null;
        try
        {
            using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
            options.AppendExecutionProvider_CPU();
            session = new InferenceSession(data, options);
            if (session.InputMetadata.Count != 1 ||
                !session.InputMetadata.TryGetValue(definition.InputName, out var input) || !session.OutputMetadata.TryGetValue(definition.OutputName, out var output) ||
                input.ElementType != typeof(float) || output.ElementType != typeof(float) ||
                input.Dimensions.Length != 4 || input.Dimensions.Where((size, index) => size >= 0 && size != definition.Shape[index]).Any() ||
                output.Dimensions.All(size => size > 0) && output.Dimensions.Aggregate(1L, (count, size) => checked(count * size)) != definition.OutputCount)
                return new Err<ModelError>(new ModelContractMismatch(ModelMessages.Get("ContractMismatch")));
            var estimator = new OnnxMouthEstimator(session, definition);
            session = null;
            return new Ok<IMouthEstimator>(estimator);
        }
        catch (Exception exception) when (exception is OnnxRuntimeException or DllNotFoundException or BadImageFormatException)
        { return new Err<ModelError>(new ModelLoadFailed(exception.Message)); }
        catch (OverflowException exception) { return new Err<ModelError>(new ModelContractMismatch(exception.Message)); }
        finally { session?.Dispose(); }
    }

    public Result<MouthEstimate, ModelInferenceFailed> Estimate(ReadOnlySpan<byte> rgb24, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        OnnxInputEncoder.Encode(rgb24, input, definition);
        using var options = new RunOptions();
        using var registration = cancellationToken.Register(() => options.Terminate = true);
        try
        {
            using var output = session.Run(options, inputs, new[] { definition.OutputName });
            cancellationToken.ThrowIfCancellationRequested();
            var values = output[0].GetTensorDataAsSpan<float>();
            if (values.Length != definition.OutputCount)
                return new Err<ModelInferenceFailed>(new(ModelMessages.Get("OutputCountMismatch")));
            if (!float.IsFinite(values[definition.JawOpenIndex]))
                return new Err<ModelInferenceFailed>(new(ModelMessages.Get("JawOpenOutputInvalid")));
            if (expressionIndices.Length == 0) return new Ok<MouthEstimate>(new(values[definition.JawOpenIndex]));

            var expressions = ImmutableArray.CreateBuilder<MouthExpression>(expressionIndices.Length);
            foreach (var (shape, index) in expressionIndices)
            {
                var raw = values[index];
                if (!float.IsFinite(raw)) return new Err<ModelInferenceFailed>(new(ModelMessages.Format("ExpressionOutputInvalid", shape)));
                expressions.Add(new(shape, raw));
            }
            return new Ok<MouthEstimate>(new(values[definition.JawOpenIndex], expressions.MoveToImmutable()));
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (OnnxRuntimeException exception)
        { return new Err<ModelInferenceFailed>(new(exception.Message)); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { inputValue.Dispose(); }
        finally { session.Dispose(); }
    }
}


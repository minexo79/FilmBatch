using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FilmBatch.Mobile.Probes;

public static class OnnxProbe
{
    // Owned ONNX fixture: IR8, opset13, float32 [1,3,4,4], one Neg node: output = -input.
    // This is a native runtime arithmetic/ABI check, not a photographic denoising model.
    private const string NegationModel = "CAg6eQoUCgVpbnB1dBIGb3V0cHV0IgNOZWcSHmZpbG1iYXRjaC1wb3J0YWJpbGl0eS1uZWdhdGlvblofCgVpbnB1dBIWChQIARIQCgIIAQoCCAMKAggECgIIBGIgCgZvdXRwdXQSFgoUCAESEAoCCAEKAggDCgIIBAoCCARCAhAN";

    public static ProbeResult Run(ProbeOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OrtEnv.Instance().DisableTelemetryEvents();
        using var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        var provider = options.Provider;
        if (provider.Equals("CoreML", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS())
                return new("onnx", "blocked", "CoreML requested on a non-Apple platform; no silent provider substitution.", []);
            sessionOptions.AppendExecutionProvider("CoreML", new Dictionary<string, string>
            {
                ["ModelFormat"] = "MLProgram",
                ["MLComputeUnits"] = "ALL",
                ["RequireStaticInputShapes"] = "1"
            });
            provider = "CoreML + CPU (node placement not measured)";
        }
        else if (provider.Equals("CPU", StringComparison.OrdinalIgnoreCase)) provider = "CPU";
        else throw new ArgumentException("Provider must be CPU or CoreML.");

        using var arithmetic = new InferenceSession(Convert.FromBase64String(NegationModel), sessionOptions);
        var input = new float[48];
        for (var i = 0; i < input.Length; i++) input[i] = (i - 24) / 8f; // Negative, zero and HDR > 1.
        var output = new float[input.Length];
        using var inputValue = OrtValue.CreateTensorValueFromMemory(input, [1, 3, 4, 4]);
        using var outputValue = OrtValue.CreateTensorValueFromMemory(output, [1, 3, 4, 4]);
        Run(arithmetic, inputValue, outputValue, "input", "output", cancellationToken);
        CheckNegation(input, output);

        // A pre-terminated native call may throw, or a fused provider may finish successfully.
        // In both cases the caller must discard a cancelled result; the next fresh RunOptions must work.
        var nativeAborted = false;
        var cancelledDiscarded = false;
        using (var cancelled = new CancellationTokenSource())
        using (var runOptions = new RunOptions { Terminate = true })
        {
            cancelled.Cancel();
            try
            {
                try { arithmetic.Run(runOptions, ["input"], [inputValue], ["output"], [outputValue]); }
                catch (OnnxRuntimeException) { nativeAborted = true; }
                cancelled.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { cancelledDiscarded = true; }
        }
        Array.Clear(output);
        Run(arithmetic, inputValue, outputValue, "input", "output", cancellationToken);
        CheckNegation(input, output);
        if (!cancelledDiscarded) throw new InvalidDataException("Cancelled ONNX result was not discarded.");

        var metrics = new Dictionary<string, string>
        {
            ["RuntimeVersion"] = OrtEnv.Instance().GetVersionString(),
            ["RequestedProvider"] = options.Provider,
            ["ProviderBoundary"] = provider,
            ["ArithmeticValuesChecked"] = input.Length.ToString(CultureInfo.InvariantCulture),
            ["PreterminatedNativeCallAborted"] = nativeAborted.ToString(),
            ["CancelledResultDiscarded"] = cancelledDiscarded.ToString(),
            ["SessionReuse"] = "New RunOptions; all arithmetic outputs matched after cancellation",
            ["ModelQualityBoundary"] = "Arithmetic fixture is not AI denoising; no iPad or full-network CoreML placement claim."
        };

        if (options.ModelPath is { Length: > 0 } path)
        {
            using var beforeStream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(beforeStream));
            beforeStream.Close();
            using var model = new InferenceSession(path, sessionOptions);
            if (model.InputMetadata.Count != 1 || model.OutputMetadata.Count != 1)
                throw new InvalidDataException("Optional model probe requires one input and one output tensor.");
            var inputNode = model.InputMetadata.Single();
            var outputNode = model.OutputMetadata.Single();
            var shape = inputNode.Value.Dimensions;
            if (inputNode.Value.ElementDataType != TensorElementType.Float || outputNode.Value.ElementDataType != TensorElementType.Float
                || shape.Length != 4 || shape[0] != 1 || shape[1] != 3 || shape.Any(dimension => dimension <= 0)
                || !shape.SequenceEqual(outputNode.Value.Dimensions))
                throw new InvalidDataException("Optional model requires fixed-shape FP32 [1,3,H,W] input and same-shaped output.");
            var count = checked(shape[0] * shape[1] * shape[2] * shape[3]);
            var pixels = new float[count];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = 0.1f + i % 257 / 320f;
            var result = new float[count];
            var tensorShape = Array.ConvertAll(shape, value => (long)value);
            using var valuesIn = OrtValue.CreateTensorValueFromMemory(pixels, tensorShape);
            using var valuesOut = OrtValue.CreateTensorValueFromMemory(result, tensorShape);
            var timer = Stopwatch.StartNew();
            Run(model, valuesIn, valuesOut, inputNode.Key, outputNode.Key, cancellationToken);
            timer.Stop();
            var minimum = float.PositiveInfinity;
            var maximum = float.NegativeInfinity;
            foreach (var value in result)
            {
                if (!float.IsFinite(value)) throw new InvalidDataException("Actual model produced a non-finite value.");
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
            using var afterStream = File.OpenRead(path);
            if (hash != Convert.ToHexStringLower(SHA256.HashData(afterStream)))
                throw new InvalidDataException("Model file changed during inference.");
            metrics["ModelFile"] = Path.GetFullPath(path);
            metrics["ModelSha256"] = hash;
            metrics["InputShape"] = string.Join(',', shape);
            metrics["ModelOutputValuesChecked"] = count.ToString(CultureInfo.InvariantCulture);
            metrics["ModelOutputMin"] = minimum.ToString("R", CultureInfo.InvariantCulture);
            metrics["ModelOutputMax"] = maximum.ToString("R", CultureInfo.InvariantCulture);
            metrics["ModelRunMilliseconds"] = timer.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
            metrics["ModelQualityBoundary"] = "Actual weights executed on deterministic synthetic input; not photographic quality, tiling, or full-image memory validation.";
        }
        else metrics["ActualPhotographicModel"] = "Not requested; use --model with an existing ONNX file.";
        return new("onnx", "pass", "Native ONNX values, cancellation-result discard and session reuse verified.", metrics);
    }

    private static void Run(InferenceSession session, OrtValue input, OrtValue output,
        string inputName, string outputName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var run = new RunOptions();
        using var registration = cancellationToken.Register(() => run.Terminate = true);
        try
        {
            session.Run(run, [inputName], [input], [outputName], [output]);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OnnxRuntimeException error) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("ONNX inference cancelled.", error, cancellationToken);
        }
    }

    private static void CheckNegation(float[] input, float[] output)
    {
        for (var i = 0; i < input.Length; i++)
            if (output[i] != -input[i]) throw new InvalidDataException($"ONNX value {i}: {output[i]} != {-input[i]}");
    }
}

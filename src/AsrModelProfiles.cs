namespace A1IndexTTSMod;

internal sealed record AsrModelProfile(string Id, string Label, string DirectoryName,
    string Encoder, string Decoder, string Joiner, string Precision)
{
    internal string ModelDirectory(string gameRoot) => Path.Combine(gameRoot, "A1IndexTTSMod", "asr", DirectoryName);
    internal bool UsesBpe => Id == "accurate160m";
    internal bool IsInstalled(string gameRoot) => new[] { Encoder, Decoder, Joiner, "tokens.txt" }
        .Concat(UsesBpe ? new[] { "bpe.model" } : Array.Empty<string>())
        .All(name => File.Exists(Path.Combine(ModelDirectory(gameRoot), name)));
}

internal static class AsrModelProfiles
{
    internal static readonly AsrModelProfile Lightweight = new("lightweight14m", "轻量 · 14M",
        "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23", "encoder-epoch-99-avg-1.onnx",
        "decoder-epoch-99-avg-1.onnx", "joiner-epoch-99-avg-1.onnx", "FP32");
    internal static readonly AsrModelProfile Accurate = new("accurate160m", "准确 · 160M",
        "sherpa-onnx-streaming-zipformer-zh-fp16-2025-06-30", "encoder.fp16.onnx",
        "decoder.fp16.onnx", "joiner.fp16.onnx", "FP16");
    internal static readonly AsrModelProfile[] All = { Lightweight, Accurate };
    internal static AsrModelProfile Resolve(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Lightweight;
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace A1IndexTTSMod;

/// <summary>The pinned Chinese unigram vocabulary, exported without Python/protobuf runtime dependencies.
/// Encoding mirrors simple-sentencepiece v0.7 used by sherpa-onnx 1.13.8, including UTF-8 byte fallback.</summary>
internal sealed class AsrBpeTokenizer
{
    internal const string ModelSha256 = "867a7355801cb43939962ad757ba1cb7941b6171b5a6902772483b4e3a623377";
    private readonly List<(string Text, float Score)> _pieces = new();
    private readonly List<(byte[] Bytes, string Text, float Score)> _matches = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly UTF8Encoding _utf8 = new(false, true);

    internal AsrBpeTokenizer(string modelPath, string tokensPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("准确档缺少 BPE 分词资源，请重新运行 Install-ASR.ps1 -Profile accurate160m。", modelPath);
        var data = File.ReadAllBytes(modelPath);
        if (Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() != ModelSha256)
            throw new InvalidDataException("BPE 分词资源 SHA256 不匹配，请重新安装准确档。");
        var offset = 0;
        while (offset < data.Length)
        {
            var tag = Varint(data, ref offset, data.Length);
            if (tag != 10) { Skip(data, ref offset, data.Length, tag & 7); continue; }
            var end = BlockEnd(data, ref offset, data.Length);
            string? text = null; float score = 0;
            while (offset < end)
            {
                var field = Varint(data, ref offset, end);
                if (field == 10)
                {
                    var textEnd = BlockEnd(data, ref offset, end);
                    text = _utf8.GetString(data, offset, textEnd - offset); offset = textEnd;
                }
                else if (field == 21)
                {
                    Require(offset, 4, end); score = BitConverter.ToSingle(data, offset); offset += 4;
                }
                else Skip(data, ref offset, end, field & 7);
            }
            if (string.IsNullOrEmpty(text) || !float.IsFinite(score)) throw new InvalidDataException("Invalid SentencePiece vocabulary entry.");
            _ids.Add(text, _pieces.Count); _pieces.Add((text, score));
            _matches.Add((_utf8.GetBytes(text), text, score));
        }
        // Check IDs as well as spelling: an unrelated tokenizer must never be used with these weights.
        var symbols = File.ReadLines(tokensPath).Select(line => line.TrimEnd('\r').Split(' ')).ToArray();
        if (_pieces.Count != 2000 || symbols.Length != 2002) throw new InvalidDataException("BPE/token vocabulary size mismatch.");
        for (var i = 0; i < _pieces.Count; i++)
            if (symbols[i].Length != 2 || symbols[i][0] != _pieces[i].Text || symbols[i][1] != i.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException($"BPE/token vocabulary mismatch at ID {i}.");
        for (var i = 0; i < 256; i++)
            if (_pieces[i + 3].Text != $"<0x{i:X2}>") throw new InvalidDataException("BPE byte fallback table is incomplete.");
    }

    internal void ExportVocabulary(string path) => File.WriteAllLines(path,
        _pieces.Select(piece => piece.Text + "\t" + piece.Score.ToString("R", CultureInfo.InvariantCulture)), new UTF8Encoding(false));

    // sherpa's text hotword API always prepends ▁ for BPE. Chinese speech normally
    // emits it only at the utterance start, so that API cannot bias names mid-sentence.
    // Its pinned SymbolTable keeps the FIRST spelling for each ID. Additional PUA
    // aliases let cjkchar encode exact pre-tokenized paths without changing result
    // spelling or model IDs. Originals stay first; only the derived file is changed.
    internal void ExportNativeTokens(string tokensPath, string target) => File.WriteAllLines(target,
        File.ReadLines(tokensPath).Concat(Enumerable.Range(0, _pieces.Count)
            .Select(id => char.ConvertFromUtf32(0xe000 + id) + " " + id.ToString(CultureInfo.InvariantCulture))), new UTF8Encoding(false));

    internal string NativePath(IEnumerable<string> pieces)
    {
        return string.Concat(pieces.Select(piece => char.ConvertFromUtf32(0xe000 + _ids[piece])));
    }

    internal string[] Encode(string word, bool atUtteranceStart = true)
    {
        // The game lexicon contains Chinese words without whitespace. Refuse unsupported input
        // rather than silently claiming full SentencePiece normalization for arbitrary text.
        if (word.EnumerateRunes().Any(r => r.Value < 0x3400 || r.Value > 0x9fff))
            throw new ArgumentException("当前 BPE 热词适配仅接受中文词组。", nameof(word));
        var bytes = _utf8.GetBytes((atUtteranceStart ? "▁" : "") + word);
        var scores = new float[bytes.Length + 1]; var next = new int[bytes.Length]; var texts = new string?[bytes.Length];
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            var best = float.NegativeInfinity; next[i] = -1;
            foreach (var piece in _matches)
            {
                var end = i + piece.Bytes.Length;
                if (end > bytes.Length || !bytes.AsSpan(i, piece.Bytes.Length).SequenceEqual(piece.Bytes)) continue;
                var score = piece.Score + scores[end];
                if (score > best || (score == best && next[i] >= end))
                { best = score; next[i] = end; texts[i] = piece.Text; }
            }
            scores[i] = float.IsNegativeInfinity(best) ? 0 : best;
        }
        var result = new List<string>();
        for (var i = 0; i < bytes.Length;)
        {
            if (next[i] < 0) { result.Add(_pieces[bytes[i] + 3].Text); i++; }
            else { result.Add(texts[i]!); i = next[i]; }
        }
        return result.ToArray();
    }

    private static int Varint(byte[] data, ref int offset, int end)
    {
        uint value = 0;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            Require(offset, 1, end); var b = data[offset++]; value |= (uint)(b & 127) << shift;
            if ((b & 128) == 0) return checked((int)value);
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }
    private static int BlockEnd(byte[] data, ref int offset, int end)
    { var size = Varint(data, ref offset, end); Require(offset, size, end); return offset + size; }
    private static void Skip(byte[] data, ref int offset, int end, int wire)
    {
        switch (wire)
        {
            case 0: Varint(data, ref offset, end); break;
            case 1: Require(offset, 8, end); offset += 8; break;
            case 2: offset = BlockEnd(data, ref offset, end); break;
            case 5: Require(offset, 4, end); offset += 4; break;
            default: throw new InvalidDataException("Unsupported protobuf wire type.");
        }
    }
    private static void Require(int offset, int size, int end)
    { if (size < 0 || offset < 0 || offset > end - size) throw new InvalidDataException("Truncated SentencePiece model."); }
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlujDrive.Infrastructure.Semantic;

internal sealed partial class ClipTokenizer
{
    private readonly IReadOnlyDictionary<string, int> _vocabulary;
    private readonly IReadOnlyDictionary<string, int> _mergeRanks;
    private readonly string[] _byteEncoder;
    private readonly ConcurrentDictionary<string, string[]> _cache = new(StringComparer.Ordinal);
    private readonly int _contextLength;
    private readonly int _startTokenId;
    private readonly int _endTokenId;

    public ClipTokenizer(string modelPath, SemanticModelManifest manifest)
    {
        _contextLength = manifest.ContextLength;
        _startTokenId = manifest.StartTokenId;
        _endTokenId = manifest.EndTokenId;
        _vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(
                          File.ReadAllText(ResolveContainedPath(modelPath, manifest.VocabularyFile))) ??
                      throw new InvalidDataException("The TinyCLIP vocabulary is invalid.");
        _mergeRanks = File.ReadLines(ResolveContainedPath(modelPath, manifest.MergesFile))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select((line, rank) => (Parts: line.Split(' ', StringSplitOptions.RemoveEmptyEntries), Rank: rank))
            .Where(item => item.Parts.Length == 2)
            .ToDictionary(item => PairKey(item.Parts[0], item.Parts[1]), item => item.Rank);
        _byteEncoder = BuildByteEncoder();
    }

    public int[] Encode(string text)
    {
        var tokens = new List<int>(_contextLength) { _startTokenId };
        var cleaned = Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");

        foreach (Match match in TokenPattern().Matches(cleaned))
        {
            var encoded = string.Concat(Encoding.UTF8.GetBytes(match.Value).Select(value => _byteEncoder[value]));
            foreach (var piece in _cache.GetOrAdd(encoded, ApplyBpe))
            {
                if (_vocabulary.TryGetValue(piece, out var id))
                {
                    tokens.Add(id);
                }
            }
        }

        if (tokens.Count >= _contextLength)
        {
            tokens.RemoveRange(_contextLength - 1, tokens.Count - (_contextLength - 1));
        }

        tokens.Add(_endTokenId);
        while (tokens.Count < _contextLength)
        {
            tokens.Add(0);
        }

        return tokens.ToArray();
    }

    private string[] ApplyBpe(string token)
    {
        var word = token.EnumerateRunes().Select(rune => rune.ToString()).ToList();
        if (word.Count == 0)
        {
            return [];
        }

        word[^1] += "</w>";

        while (word.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestIndex = -1;

            for (var index = 0; index < word.Count - 1; index++)
            {
                if (_mergeRanks.TryGetValue(PairKey(word[index], word[index + 1]), out var rank) &&
                    rank < bestRank)
                {
                    bestRank = rank;
                    bestIndex = index;
                }
            }

            if (bestIndex < 0)
            {
                break;
            }

            word[bestIndex] += word[bestIndex + 1];
            word.RemoveAt(bestIndex + 1);
        }

        return word.ToArray();
    }

    private static string[] BuildByteEncoder()
    {
        var bytes = Enumerable.Range('!', '~' - '!' + 1)
            .Concat(Enumerable.Range('¡', '¬' - '¡' + 1))
            .Concat(Enumerable.Range('®', 'ÿ' - '®' + 1))
            .ToList();
        var codePoints = new List<int>(bytes);
        var extra = 0;

        for (var value = 0; value < 256; value++)
        {
            if (bytes.Contains(value))
            {
                continue;
            }

            bytes.Add(value);
            codePoints.Add(256 + extra++);
        }

        var encoder = new string[256];
        for (var index = 0; index < bytes.Count; index++)
        {
            encoder[bytes[index]] = char.ConvertFromUtf32(codePoints[index]);
        }

        return encoder;
    }

    private static string PairKey(string left, string right) => $"{left}\0{right}";

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The tokenizer path escapes the model directory.");
        }

        return fullPath;
    }

    [GeneratedRegex(@"'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]+|[^\s\p{L}\p{N}]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
}

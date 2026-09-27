using System;
using System.Collections.Generic;

namespace WpfApp1;

/// <summary>
/// Compact BM25 index — pure C# port of Solution5's bm25_index.h.
/// Inverted index + ranking, no external dependencies.
/// Tokenizes Cyrillic (UTF‑8 continuation bytes count as letters),
/// only ASCII a‑z are lowercased. Minimum token length = 2.
/// </summary>
public class Bm25Index
{
    public float K1 { get; set; } = 1.2f;
    public float B  { get; set; } = 0.75f;

    private readonly List<int> _docLengths = new();
    private float _averageDocLength;

    // term → [(docId, tf), …]
    private readonly Dictionary<string, List<(int docId, int tf)>> _postingLists = new();
    // term → document frequency
    private readonly Dictionary<string, int> _df = new();

    // --- public state --------------------------------------------------------

    public int DocCount => _docLengths.Count;
    public float AverageDocLength => _averageDocLength;

    // --- tokenizer -----------------------------------------------------------

    /// <summary>
    /// Simple tokenizer: ASCII a‑z / A‑Z and all bytes ≥ 0x80 (Cyrillic UTF‑8)
    /// are considered letters. ASCII letters are lowercased. Tokens shorter
    /// than 2 chars are discarded.
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var cur = new char[256]; // reusable buffer
        int pos = 0;

        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c >= 0x80)
            {
                cur[pos++] = (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
                i++;
                // grab continuation bytes of a UTF‑8 multibyte sequence
                while (i < text.Length && (text[i] & 0xC0) == 0x80)
                {
                    cur[pos++] = text[i];
                    i++;
                }
            }
            else
            {
                if (pos >= 2) tokens.Add(new string(cur, 0, pos));
                pos = 0;
                i++;
            }
        }

        if (pos >= 2) tokens.Add(new string(cur, 0, pos));
        return tokens;
    }

    // --- indexing ------------------------------------------------------------

    public void AddDocument(string text)
    {
        var tokens = Tokenize(text);

        _docLengths.Add(tokens.Count);
        int docId = _docLengths.Count - 1;

        // term frequency within this document
        var tf = new Dictionary<string, int>(tokens.Count);
        foreach (var t in tokens)
        {
            tf.TryGetValue(t, out int count);
            tf[t] = count + 1;
        }

        foreach (var (term, freq) in tf)
        {
            if (!_postingLists.TryGetValue(term, out var list))
            {
                list = new List<(int, int)>();
                _postingLists[term] = list;
            }
            list.Add((docId, freq));

            _df.TryGetValue(term, out int df);
            _df[term] = df + 1;
        }

        // recompute running average (kept cheap)
        _averageDocLength = 0f;
        foreach (var len in _docLengths) _averageDocLength += len;
        if (_docLengths.Count > 0)
            _averageDocLength /= _docLengths.Count;
    }

    // --- scoring & search ----------------------------------------------------

    private float ScoreDoc(int docId, List<string> queryTerms)
    {
        float score = 0f;
        int dl = _docLengths[docId];

        foreach (var term in queryTerms)
        {
            if (!_df.TryGetValue(term, out int df)) continue;

            float idf = MathF.Log(
                1f + (DocCount - df + 0.5f) / (df + 0.5f));

            if (!_postingLists.TryGetValue(term, out var postings)) continue;

            int tf = 0;
            foreach (var (did, f) in postings)
                if (did == docId) { tf = f; break; }

            if (tf == 0) continue;

            float avgdl = _averageDocLength > 0 ? _averageDocLength : 1f;
            float num = tf * (K1 + 1f);
            float den = tf + K1 * (1f - B + B * dl / avgdl);
            score += idf * num / den;
        }

        return score;
    }

    /// <summary>
    /// Returns top-k scored (docId, score). Empty if no query match.
    /// </summary>
    public List<(int docId, float score)> Search(string query, int k)
    {
        var qterms = Tokenize(query);
        if (qterms.Count == 0) return new List<(int, float)>();

        var scored = new List<(int, float)>(_docLengths.Count);
        for (int i = 0; i < _docLengths.Count; i++)
        {
            float s = ScoreDoc(i, qterms);
            if (s > 0f) scored.Add((i, s));
        }

        // top-k partial sort (Item2 = score, named tuple fields not available in lambda body)
        int n = Math.Min(k, scored.Count);
        scored.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        if (n < scored.Count)
            scored.RemoveRange(n, scored.Count - n);

        return scored;
    }
}
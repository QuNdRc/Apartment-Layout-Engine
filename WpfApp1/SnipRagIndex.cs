using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WpfApp1;

/// <summary>
/// Lightweight RAG index over SNiP/SP building norms from snip_norms/ folder.
/// Reads all *.txt files, chunks each by paragraphs, indexes via BM25.
/// Thread‑safe lazy init on first query.
/// </summary>
public static class SnipRagIndex
{
    private static Bm25Index? _index;
    private static string[] _chunks = Array.Empty<string>();
    private static readonly object _lock = new();
    private static bool _loaded;

    // --- helpers -------------------------------------------------------------

    /// <summary>
    /// Resolves the norms directory — search order:
    /// 1. <exe>/snip_norms/
    /// 2. <repo root>/snip_norms/
    /// Returns empty string if the directory is missing or empty.
    /// </summary>
    private static string GetNormsDir()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidate = Path.Combine(exeDir, "snip_norms");
        if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.txt").Any())
            return candidate;

        // Fallback: two levels up from exe dir = repo root (dev environment)
        var repoRoot = Path.GetFullPath(Path.Combine(exeDir, "..", "..", ".."));
        candidate = Path.Combine(repoRoot, "snip_norms");
        return Directory.Exists(candidate) ? candidate : "";
    }

    /// <summary>
    /// Splits text on blank lines (≥2 consecutive newlines), trims,
    /// discards chunks shorter than 10 characters.
    /// </summary>
    private static string[] ChunkText(string text)
    {
        var rawParagraphs = text.Split(
            new[] { "\r\n\r\n", "\n\n" },
            StringSplitOptions.RemoveEmptyEntries);

        var chunks = new List<string>(rawParagraphs.Length);
        foreach (var para in rawParagraphs)
        {
            var trimmed = para.Trim();
            if (trimmed.Length >= 10)
                chunks.Add(trimmed);
        }
        return chunks.ToArray();
    }

    // --- init ----------------------------------------------------------------

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;

            var dir = GetNormsDir();
            if (string.IsNullOrEmpty(dir))
            {
                _loaded = true;
                return;
            }

            var files = Directory.GetFiles(dir, "*.txt", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                _loaded = true;
                return;
            }

            var allChunks = new List<string>();
            foreach (var path in files)
            {
                try
                {
                    var text = File.ReadAllText(path, Encoding.UTF8);
                    allChunks.AddRange(ChunkText(text));
                }
                catch
                {
                    // skip unreadable files
                }
            }

            _chunks = allChunks.ToArray();
            _index = new Bm25Index();
            foreach (var chunk in _chunks)
                _index.AddDocument(chunk);

            _loaded = true;
        }
    }

    // --- public API ----------------------------------------------------------

    /// <summary>
    /// Returns up to 'k' top-scoring chunks concatenated with double newlines,
    /// or empty string if the index is empty or no chunks match.
    /// </summary>
    public static string Search(string query, int k = 3)
    {
        EnsureLoaded();
        if (_index == null || _index.DocCount == 0 || string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var results = _index.Search(query, k);
        if (results.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var (docId, _) in results)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(_chunks[docId]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Number of indexed chunks (documents). 0 if snip_rules.txt was not found.
    /// </summary>
    public static int ChunkCount
    {
        get { EnsureLoaded(); return _index?.DocCount ?? 0; }
    }
}
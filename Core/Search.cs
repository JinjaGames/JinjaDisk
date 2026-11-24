using System;
using System.Collections.Generic;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JinjaDisk.Core;

/// <summary>
/// Everything tarzı anlık arama. Desteklenen sözdizimi:
///   kelime1 kelime2      -> hepsi adda geçmeli (VE)
///   *.mp4  rapor?.pdf    -> joker karakter (tam ad)
///   !kelime              -> hariç tut
///   ext:mp4;mkv          -> uzantı
///   size:>1gb  size:<10mb -> boyut
///   folder: / file:      -> sadece klasör / dosya
///   \Downloads\          -> yolda geçen (ters bölü içeren terim)
///   "tam ifade"          -> boşluklu ifade
/// </summary>
public sealed class SearchQuery
{
    enum Kind { Sub, Wild, Path }
    readonly record struct Term(Kind K, string Text, bool Neg);

    readonly List<Term> terms = new();
    HashSet<string> exts;
    long minSize = -1, maxSize = -1;
    int onlyType; // 0 hepsi, 1 klasör, 2 dosya
    public bool IsEmpty => terms.Count == 0 && exts == null && minSize < 0 && maxSize < 0 && onlyType == 0;

    public static SearchQuery Parse(string text)
    {
        var q = new SearchQuery();
        foreach (var raw in Tokenize(text ?? ""))
        {
            var t = raw;
            bool neg = false;
            if (t.StartsWith('!') && t.Length > 1) { neg = true; t = t[1..]; }
            var lower = t.ToLowerInvariant();
            if (lower.StartsWith("ext:"))
            {
                q.exts ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in t[4..].Split(';', ',', '|'))
                    if (e.Trim().TrimStart('.', '*').Length > 0) q.exts.Add(e.Trim().TrimStart('*', '.'));
                continue;
            }
            if (lower.StartsWith("size:")) { q.ParseSize(lower[5..]); continue; }
            if (lower is "folder:" or "dir:" or "klasör:" or "klasor:") { q.onlyType = 1; continue; }
            if (lower is "file:" or "dosya:") { q.onlyType = 2; continue; }
            if (t.Contains('\\')) q.terms.Add(new Term(Kind.Path, t, neg));
            else if (t.IndexOfAny(new[] { '*', '?' }) >= 0) q.terms.Add(new Term(Kind.Wild, t, neg));
            else q.terms.Add(new Term(Kind.Sub, t, neg));
        }
        return q;
    }

    static IEnumerable<string> Tokenize(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            var sb = new System.Text.StringBuilder();
            bool quoted = false;
            while (i < s.Length && (quoted || !char.IsWhiteSpace(s[i])))
            {
                if (s[i] == '"') quoted = !quoted; else sb.Append(s[i]);
                i++;
            }
            if (sb.Length > 0) yield return sb.ToString();
        }
    }

    void ParseSize(string s)
    {
        s = s.Trim();
        if (s.Contains('-') && !s.StartsWith('-'))
        {
            var parts = s.Split('-', 2);
            minSize = ParseBytes(parts[0]); maxSize = ParseBytes(parts[1]);
            return;
        }
        if (s.StartsWith(">=")) minSize = ParseBytes(s[2..]);
        else if (s.StartsWith("<=")) maxSize = ParseBytes(s[2..]);
        else if (s.StartsWith('>')) minSize = ParseBytes(s[1..]) + 1;
        else if (s.StartsWith('<')) maxSize = Math.Max(0, ParseBytes(s[1..]) - 1);
        else { var v = ParseBytes(s); minSize = v; maxSize = v; }
    }

    static long ParseBytes(string s)
    {
        s = s.Trim().ToLowerInvariant().Replace(',', '.');
        long mul = 1;
        string[] units = { "tb", "gb", "mb", "kb", "t", "g", "m", "k", "b" };
        long[] muls = { 1L << 40, 1L << 30, 1L << 20, 1L << 10, 1L << 40, 1L << 30, 1L << 20, 1L << 10, 1 };
        for (int i = 0; i < units.Length; i++)
            if (s.EndsWith(units[i])) { mul = muls[i]; s = s[..^units[i].Length]; break; }
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? (long)(v * mul) : 0;
    }

    bool Match(FileIndex idx, int i)
    {
        byte f = idx.Flags[i];
        if ((f & FileIndex.FDeleted) != 0 || i == idx.Root) return false;
        bool dir = (f & FileIndex.FDir) != 0;
        if (onlyType == 1 && !dir) return false;
        if (onlyType == 2 && dir) return false;
        if (exts != null)
        {
            if (dir) return false;
            var e = idx.ExtSpan(i);
            if (e.IsEmpty) return false;
            bool ok = false;
            foreach (var x in exts) if (e.Equals(x, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
            if (!ok) return false;
        }
        if (minSize >= 0 && idx.Size[i] < minSize) return false;
        if (maxSize >= 0 && idx.Size[i] > maxSize) return false;

        var name = idx.NameSpan(i);
        string path = null;
        foreach (var t in terms)
        {
            bool hit = t.K switch
            {
                Kind.Sub => name.Contains(t.Text, StringComparison.OrdinalIgnoreCase),
                Kind.Wild => FileSystemName.MatchesSimpleExpression(t.Text, name, true),
                _ => (path ??= idx.GetPath(i)).Contains(t.Text, StringComparison.OrdinalIgnoreCase)
            };
            if (hit == t.Neg) return false;
        }
        return true;
    }

    public int[] Run(FileIndex idx, CancellationToken ct)
    {
        if (IsEmpty) return Array.Empty<int>();
        // Ucuz terimler önce: alt dize < joker < yol
        terms.Sort((a, b) => a.K.CompareTo(b.K));
        const int chunk = 1 << 15;
        int parts = (idx.Count + chunk - 1) / chunk;
        var results = new List<int>[parts];
        Parallel.For(0, parts, new ParallelOptions { CancellationToken = ct }, p =>
        {
            var local = new List<int>();
            int s = p * chunk, e = Math.Min(idx.Count, s + chunk);
            for (int i = s; i < e; i++)
            {
                if ((i & 4095) == 0 && ct.IsCancellationRequested) return;
                if (Match(idx, i)) local.Add(i);
            }
            results[p] = local;
        });
        ct.ThrowIfCancellationRequested();
        int total = results.Sum(r => r?.Count ?? 0);
        var arr = new int[total];
        int pos = 0;
        foreach (var r in results)
        {
            if (r == null) continue;
            r.CopyTo(arr, pos);
            pos += r.Count;
        }
        return arr;
    }
}

public enum SortKey { Name, Folder, Size, Date }

public static class ResultSorter
{
    public static void Sort(FileIndex idx, int[] items, SortKey key, bool desc, CancellationToken ct)
    {
        Comparison<int> cmp = key switch
        {
            SortKey.Size => (a, b) => idx.Size[a].CompareTo(idx.Size[b]),
            SortKey.Date => (a, b) => idx.Mtime[a].CompareTo(idx.Mtime[b]),
            SortKey.Folder => (a, b) =>
            {
                int r = idx.Parent[a].CompareTo(idx.Parent[b]);
                return r != 0 ? r : idx.NameSpan(a).CompareTo(idx.NameSpan(b), StringComparison.OrdinalIgnoreCase);
            },
            _ => (a, b) => idx.NameSpan(a).CompareTo(idx.NameSpan(b), StringComparison.OrdinalIgnoreCase)
        };
        if (key == SortKey.Folder)
        {
            // Klasör sıralaması için yolları bir kez hesapla
            var paths = new Dictionary<int, string>();
            foreach (var i in items)
            {
                int p = idx.Parent[i];
                if (!paths.ContainsKey(p)) paths[p] = idx.GetPath(p);
            }
            cmp = (a, b) =>
            {
                int r = string.Compare(paths[idx.Parent[a]], paths[idx.Parent[b]], StringComparison.OrdinalIgnoreCase);
                return r != 0 ? r : idx.NameSpan(a).CompareTo(idx.NameSpan(b), StringComparison.OrdinalIgnoreCase);
            };
        }
        var final = desc ? (a, b) => cmp(b, a) : cmp;
        ParallelSort(items, final, ct);
    }

    // Parçalı paralel sıralama + birleştirme
    static void ParallelSort(int[] a, Comparison<int> cmp, CancellationToken ct)
    {
        int n = a.Length;
        if (n < 50_000) { Array.Sort(a, cmp); return; }
        int parts = Math.Min(Environment.ProcessorCount, 16);
        int size = (n + parts - 1) / parts;
        Parallel.For(0, parts, p =>
        {
            int s = p * size, len = Math.Min(size, n - s);
            if (len > 0) Array.Sort(a, s, len, Comparer<int>.Create(cmp));
        });
        ct.ThrowIfCancellationRequested();
        var tmp = new int[n];
        for (int width = size; width < n; width *= 2)
        {
            Parallel.For(0, (n + 2 * width - 1) / (2 * width), k =>
            {
                int lo = k * 2 * width, mid = Math.Min(lo + width, n), hi = Math.Min(lo + 2 * width, n);
                int i = lo, j = mid, o = lo;
                while (i < mid && j < hi) tmp[o++] = cmp(a[i], a[j]) <= 0 ? a[i++] : a[j++];
                while (i < mid) tmp[o++] = a[i++];
                while (j < hi) tmp[o++] = a[j++];
            });
            Array.Copy(tmp, a, n);
            ct.ThrowIfCancellationRequested();
        }
    }
}

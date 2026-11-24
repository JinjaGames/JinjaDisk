using System;
using System.Collections.Generic;

namespace JinjaDisk.Core;

public sealed class ExtStat
{
    public string Ext;
    public Category Cat;
    public long Size;
    public long Count;
}

public sealed class DiskStats
{
    public List<ExtStat> Exts = new();
    public long[] CatSize = new long[10];
    public long[] CatCount = new long[10];
    public List<int> TopFiles = new();
    public long TotalAlloc;

    public static DiskStats Compute(FileIndex idx, int topN = 300)
    {
        var s = new DiskStats();
        var dict = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
        var look = dict.GetAlternateLookup<ReadOnlySpan<char>>();
        var heap = new PriorityQueue<int, long>();
        for (int i = 0; i < idx.Count; i++)
        {
            byte f = idx.Flags[i];
            if ((f & (FileIndex.FDir | FileIndex.FDeleted)) != 0) continue;
            long a = idx.Alloc[i];
            var cat = Categories.Of(idx, i);
            s.CatSize[(int)cat] += a;
            s.CatCount[(int)cat]++;
            s.TotalAlloc += a;

            var ext = idx.ExtSpan(i);
            if (ext.Length > 16) ext = ReadOnlySpan<char>.Empty;
            if (!look.TryGetValue(ext, out var es))
            {
                var key = ext.ToString().ToLowerInvariant();
                es = new ExtStat { Ext = key, Cat = ext.IsEmpty ? Category.Other : cat };
                dict[key] = es;
            }
            es.Size += a;
            es.Count++;

            if (heap.Count < topN) heap.Enqueue(i, a);
            else if (a > idx.Alloc[heap.Peek()]) heap.EnqueueDequeue(i, a);
        }
        s.Exts.AddRange(dict.Values);
        s.Exts.Sort((x, y) => y.Size.CompareTo(x.Size));
        while (heap.Count > 0) s.TopFiles.Add(heap.Dequeue());
        s.TopFiles.Reverse();
        return s;
    }
}

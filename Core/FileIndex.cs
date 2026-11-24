using System;
using System.Collections.Generic;

namespace JinjaDisk.Core;

/// <summary>
/// Bellekte tutulan dosya ağacı. Struct-of-arrays düzeni: milyonlarca kayıt için
/// nesne başına yük yok, isimler tek bir char havuzunda.
/// </summary>
public sealed class FileIndex
{
    public const byte FDir = 1, FDeleted = 2;

    public string RootPath = "";      // "C:\" veya "D:\Oyunlar\"
    public int Count;
    public int Root;
    public int[] Parent;
    public int[] FirstChild;
    public int[] NextSibling;
    public char[] NamePool;
    public int[] NameOff;
    public byte[] NameLen;
    public long[] Size;    // mantıksal boyut (klasörlerde toplam)
    public long[] Alloc;   // diskte kapladığı alan (klasörlerde toplam)
    public long[] Mtime;   // FILETIME (UTC)
    public uint[] Attr;
    public byte[] Flags;
    public int[] FileCount; // klasör: altındaki dosya sayısı, dosya: 1

    public bool UsedMft;
    public TimeSpan ScanTime;
    public long ClusterSize = 4096;
    public int DirCount, FileTotal;

    public bool IsDir(int i) => (Flags[i] & FDir) != 0;
    public bool IsDeleted(int i) => (Flags[i] & FDeleted) != 0;
    public ReadOnlySpan<char> NameSpan(int i) => new(NamePool, NameOff[i], NameLen[i]);
    public string Name(int i) => new(NamePool, NameOff[i], NameLen[i]);

    public string DisplayName(int i) => i == Root ? RootPath : Name(i);

    public ReadOnlySpan<char> ExtSpan(int i)
    {
        if (IsDir(i)) return ReadOnlySpan<char>.Empty;
        var n = NameSpan(i);
        int d = n.LastIndexOf('.');
        return d <= 0 || d == n.Length - 1 ? ReadOnlySpan<char>.Empty : n[(d + 1)..];
    }

    public string GetPath(int i)
    {
        if (i == Root) return RootPath;
        Span<int> chain = stackalloc int[512];
        int n = 0, len = 0, c = i;
        while (c != Root && c >= 0 && n < chain.Length)
        {
            chain[n++] = c;
            len += NameLen[c] + 1;
            c = Parent[c];
        }
        var rootName = RootPath.AsSpan().TrimEnd('\\');
        len += rootName.Length;
        Span<char> buf = len <= 1024 ? stackalloc char[len] : new char[len];
        rootName.CopyTo(buf);
        int pos = rootName.Length;
        for (int k = n - 1; k >= 0; k--)
        {
            int x = chain[k];
            buf[pos++] = '\\';
            NamePool.AsSpan(NameOff[x], NameLen[x]).CopyTo(buf[pos..]);
            pos += NameLen[x];
        }
        return new string(buf[..pos]);
    }

    public string GetParentPath(int i) => Parent[i] >= 0 ? GetPath(Parent[i]) : "";

    public IEnumerable<int> Children(int i)
    {
        for (int c = FirstChild[i]; c >= 0; c = NextSibling[c])
            if ((Flags[c] & FDeleted) == 0) yield return c;
    }

    public List<int> ChildrenBySize(int i)
    {
        var list = new List<int>();
        for (int c = FirstChild[i]; c >= 0; c = NextSibling[c])
            if ((Flags[c] & FDeleted) == 0) list.Add(c);
        list.Sort((a, b) =>
        {
            int r = Alloc[b].CompareTo(Alloc[a]);
            return r != 0 ? r : Size[b].CompareTo(Size[a]);
        });
        return list;
    }

    public bool HasChildren(int i)
    {
        for (int c = FirstChild[i]; c >= 0; c = NextSibling[c])
            if ((Flags[c] & FDeleted) == 0) return true;
        return false;
    }

    public bool IsAncestor(int ancestor, int i)
    {
        for (int c = i; c >= 0; c = Parent[c])
            if (c == ancestor) return true;
        return false;
    }

    /// <summary>Yoldan indeks bulur (yoksa -1).</summary>
    public int Find(string path)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        var rp = RootPath.TrimEnd('\\');
        path = path.TrimEnd('\\');
        if (!path.StartsWith(rp, StringComparison.OrdinalIgnoreCase)) return -1;
        var rest = path.AsSpan(rp.Length).TrimStart('\\');
        int cur = Root;
        while (rest.Length > 0)
        {
            int sep = rest.IndexOf('\\');
            var part = sep < 0 ? rest : rest[..sep];
            rest = sep < 0 ? ReadOnlySpan<char>.Empty : rest[(sep + 1)..];
            int found = -1;
            for (int c = FirstChild[cur]; c >= 0; c = NextSibling[c])
            {
                if ((Flags[c] & FDeleted) != 0) continue;
                if (NameSpan(c).Equals(part, StringComparison.OrdinalIgnoreCase)) { found = c; break; }
            }
            if (found < 0) return -1;
            cur = found;
        }
        return cur;
    }

    /// <summary>Çocuk listelerini kurar ve klasör boyutlarını yukarı doğru toplar.</summary>
    public void FinalizeTree()
    {
        FirstChild = new int[Count];
        NextSibling = new int[Count];
        FileCount = new int[Count];
        Array.Fill(FirstChild, -1);
        Array.Fill(NextSibling, -1);
        DirCount = 0; FileTotal = 0;
        for (int i = Count - 1; i >= 0; i--)
        {
            int p = Parent[i];
            if (p >= 0 && p != i)
            {
                NextSibling[i] = FirstChild[p];
                FirstChild[p] = i;
            }
        }
        for (int i = 0; i < Count; i++)
        {
            if ((Flags[i] & FDir) != 0) { DirCount++; Size[i] = 0; Alloc[i] = 0; }
        }
        for (int i = 0; i < Count; i++)
        {
            if ((Flags[i] & FDir) != 0) continue;
            FileTotal++;
            FileCount[i] = 1;
            long s = Size[i], a = Alloc[i];
            int guard = 0;
            for (int p = Parent[i]; p >= 0 && guard < 4096; p = Parent[p], guard++)
            {
                Size[p] += s;
                Alloc[p] += a;
                FileCount[p]++;
                if (p == Root) break;
            }
        }
    }

    /// <summary>Silinen öğeyi ve alt ağacını işaretler, üst klasörlerin boyutunu düşer.</summary>
    public void MarkDeleted(int i)
    {
        if (i < 0 || i == Root || (Flags[i] & FDeleted) != 0) return;
        long s = Size[i], a = Alloc[i];
        int fc = FileCount[i];
        for (int p = Parent[i]; p >= 0; p = Parent[p])
        {
            Size[p] -= s; Alloc[p] -= a; FileCount[p] -= fc;
            if (p == Root) break;
        }
        var stack = new Stack<int>();
        stack.Push(i);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            Flags[x] |= FDeleted;
            if ((Flags[x] & FDir) != 0) DirCount--; else FileTotal--;
            for (int c = FirstChild[x]; c >= 0; c = NextSibling[c])
                if ((Flags[c] & FDeleted) == 0) stack.Push(c);
        }
    }
}

/// <summary>Büyüyen dizilerle FileIndex kurar.</summary>
public sealed class FileIndexBuilder
{
    int n, poolLen;
    int[] parent, off;
    byte[] len, flags;
    long[] size, alloc, mtime;
    uint[] attr;
    char[] pool;

    public int Count => n;

    public FileIndexBuilder(int capacity = 1 << 16)
    {
        capacity = Math.Max(capacity, 16);
        parent = new int[capacity]; off = new int[capacity];
        len = new byte[capacity]; flags = new byte[capacity];
        size = new long[capacity]; alloc = new long[capacity]; mtime = new long[capacity];
        attr = new uint[capacity];
        pool = new char[Math.Max(1 << 16, capacity * 16)];
    }

    void Grow()
    {
        int c = parent.Length * 2;
        Array.Resize(ref parent, c); Array.Resize(ref off, c); Array.Resize(ref len, c);
        Array.Resize(ref flags, c); Array.Resize(ref size, c); Array.Resize(ref alloc, c);
        Array.Resize(ref mtime, c); Array.Resize(ref attr, c);
    }

    public int Add(int parentIdx, ReadOnlySpan<char> name, long sz, long al, long mt, uint at, bool dir)
    {
        if (n == parent.Length) Grow();
        if (name.Length > 255) name = name[..255];
        if (poolLen + name.Length > pool.Length)
            Array.Resize(ref pool, Math.Max(pool.Length * 2, poolLen + name.Length + 1024));
        name.CopyTo(pool.AsSpan(poolLen));
        int i = n++;
        parent[i] = parentIdx; off[i] = poolLen; len[i] = (byte)name.Length;
        poolLen += name.Length;
        size[i] = sz; alloc[i] = al; mtime[i] = mt; attr[i] = at;
        flags[i] = dir ? FileIndex.FDir : (byte)0;
        return i;
    }

    public void SetParent(int i, int p) => parent[i] = p;

    public FileIndex Build(string rootPath, int root)
    {
        Array.Resize(ref parent, n); Array.Resize(ref off, n); Array.Resize(ref len, n);
        Array.Resize(ref flags, n); Array.Resize(ref size, n); Array.Resize(ref alloc, n);
        Array.Resize(ref mtime, n); Array.Resize(ref attr, n); Array.Resize(ref pool, poolLen);
        var idx = new FileIndex
        {
            RootPath = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\",
            Count = n, Root = root, Parent = parent, NamePool = pool, NameOff = off, NameLen = len,
            Size = size, Alloc = alloc, Mtime = mtime, Attr = attr, Flags = flags
        };
        idx.FinalizeTree();
        return idx;
    }
}

public readonly record struct ScanProgress(string Phase, double Fraction, long Items);

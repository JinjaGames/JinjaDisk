using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Threading;
using System.Threading.Tasks;

namespace JinjaDisk.Core;

/// <summary>
/// MFT okunamadığında (yönetici değil / NTFS değil / tek klasör) çok iş parçacıklı klasör taraması.
/// </summary>
public static class FallbackScanner
{
    readonly struct Entry
    {
        public readonly string Name;
        public readonly long Length, Mtime;
        public readonly uint Attr;
        public readonly bool IsDir;
        public Entry(string n, long l, long m, uint a, bool d) { Name = n; Length = l; Mtime = m; Attr = a; IsDir = d; }
    }

    public static FileIndex Scan(string rootPath, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        rootPath = Path.GetFullPath(rootPath);
        if (!rootPath.EndsWith('\\')) rootPath += "\\";
        long cluster = Native.GetClusterSize(Path.GetPathRoot(rootPath));

        var b = new FileIndexBuilder(1 << 18);
        object lk = new();
        int root = b.Add(-1, rootPath.TrimEnd('\\'), 0, 0, 0, (uint)FileAttributes.Directory, true);

        var work = new ConcurrentStack<(int idx, string path)>();
        work.Push((root, rootPath));
        int pending = 1;
        long items = 0;
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0,
            ReturnSpecialDirectories = false, BufferSize = 64 * 1024
        };
        var sw = Stopwatch.StartNew();
        long lastReport = 0;

        int workers = Math.Clamp(Environment.ProcessorCount, 2, 16);
        var tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            tasks[w] = Task.Factory.StartNew(() =>
            {
                var local = new List<Entry>(512);
                var subs = new List<(int, string)>(64);
                while (Volatile.Read(ref pending) > 0)
                {
                    if (ct.IsCancellationRequested) return;
                    if (!work.TryPop(out var job)) { Thread.Sleep(1); continue; }
                    local.Clear(); subs.Clear();
                    try
                    {
                        var en = new FileSystemEnumerable<Entry>(job.path,
                            (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.Length,
                                e.LastWriteTimeUtc.ToFileTime(), (uint)e.Attributes, e.IsDirectory), opts);
                        foreach (var e in en) local.Add(e);
                    }
                    catch { /* erişilemeyen klasör */ }

                    lock (lk)
                    {
                        foreach (var e in local)
                        {
                            long alloc = e.IsDir ? 0 : (e.Length + cluster - 1) / cluster * cluster;
                            int ci = b.Add(job.idx, e.Name, e.Length, alloc, e.Mtime, e.Attr, e.IsDir);
                            if (e.IsDir && (e.Attr & (uint)FileAttributes.ReparsePoint) == 0)
                                subs.Add((ci, Path.Join(job.path, e.Name)));
                        }
                    }
                    Interlocked.Add(ref pending, subs.Count);
                    foreach (var s in subs) work.Push(s);
                    Interlocked.Decrement(ref pending);
                    long total = Interlocked.Add(ref items, local.Count);
                    long ms = sw.ElapsedMilliseconds;
                    if (ms - Interlocked.Read(ref lastReport) > 150)
                    {
                        Interlocked.Exchange(ref lastReport, ms);
                        progress?.Report(new ScanProgress("Klasörler taranıyor", -1, total));
                    }
                }
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        Task.WaitAll(tasks);
        ct.ThrowIfCancellationRequested();

        progress?.Report(new ScanProgress("Ağaç kuruluyor", 0.95, items));
        var idx = b.Build(rootPath, root);
        idx.ClusterSize = cluster;
        return idx;
    }
}

public static class Scanner
{
    public static FileIndex Scan(string rootPath, IProgress<ScanProgress> progress, CancellationToken ct, out string note)
    {
        note = null;
        var sw = Stopwatch.StartNew();
        FileIndex idx = null;
        bool isDriveRoot = rootPath.Length <= 3 && rootPath.Length >= 2 && rootPath[1] == ':';
        if (isDriveRoot)
        {
            string fs = "";
            try { fs = new DriveInfo(rootPath[..1]).DriveFormat; } catch { }
            if (fs == "NTFS")
            {
                if (Native.IsElevated)
                {
                    try { idx = MftScanner.Scan(rootPath[0], progress, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { note = "MFT okunamadı (" + ex.Message + "), klasör taramasına geçildi."; }
                }
                else note = "Yönetici izni olmadığı için hızlı MFT taraması yerine klasör taraması yapıldı.";
            }
            else note = fs + " dosya sistemi: klasör taraması kullanıldı.";
        }
        idx ??= FallbackScanner.Scan(isDriveRoot ? rootPath[..2] + "\\" : rootPath, progress, ct);
        idx.ScanTime = sw.Elapsed;
        return idx;
    }
}

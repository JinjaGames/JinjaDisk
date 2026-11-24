using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace JinjaDisk.Core;

public enum CleanKind { File, Folder, FolderContents }

public sealed class TempLocation
{
    public string Title;
    public string Path;
    public long Size;
    public long Count;
}

public static class Cleanup
{
    // ---------- Geri dönüşüm kutusu ----------
    public static (long size, long count) QueryRecycleBin()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        int hr = Native.SHQueryRecycleBinW(null, ref info);
        return hr == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    public static bool EmptyRecycleBin(IntPtr hwnd)
    {
        int hr = Native.SHEmptyRecycleBinW(hwnd, null,
            Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
        return hr == 0 || hr == unchecked((int)0x8000FFFF); // E_UNEXPECTED = zaten boş
    }

    // ---------- Silme ----------
    /// <summary>Geri dönüşüm kutusuna gönderir (STA iş parçacığında çağrılmalı).</summary>
    public static bool Recycle(IEnumerable<string> paths, IntPtr hwnd)
    {
        var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (list.Count == 0) return true;
        bool ok = true;
        // Çok uzun pFrom tamponlarından kaçınmak için gruplar halinde
        foreach (var batch in list.Chunk(500))
        {
            var op = new Native.SHFILEOPSTRUCT
            {
                hwnd = hwnd,
                wFunc = Native.FO_DELETE,
                pFrom = string.Join("\0", batch) + "\0\0",
                fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT |
                                  Native.FOF_NOERRORUI | Native.FOF_WANTNUKEWARNING)
            };
            if (Native.SHFileOperationW(ref op) != 0 || op.fAnyOperationsAborted != 0) ok = false;
        }
        return ok;
    }

    public static (int ok, int failed) DeletePermanent(IEnumerable<string> paths)
    {
        int ok = 0, failed = 0;
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p)) { ForceDeleteDir(p); }
                else if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); }
                if (!File.Exists(p) && !Directory.Exists(p)) ok++; else failed++;
            }
            catch { failed++; }
        }
        return (ok, failed);
    }

    static void ForceDeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); return; }
        catch { }
        // Salt okunur öznitelikleri temizleyip tekrar dene
        foreach (var f in SafeEnum(dir, true, files: true))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        Directory.Delete(dir, true);
    }

    /// <summary>Klasörün içini temizler, kilitli dosyaları atlar. Kazanılan baytı döndürür.</summary>
    public static long CleanFolderContents(string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return 0;
        long freed = 0;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 };
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            {
                if (ct.IsCancellationRequested) return freed;
                try
                {
                    long len = f.Length;
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    f.Delete();
                    freed += len;
                }
                catch { /* kullanımda */ }
            }
            foreach (var d in new DirectoryInfo(dir).EnumerateDirectories("*", opts))
            {
                if (ct.IsCancellationRequested) return freed;
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    try { d.Delete(false); } catch { }
                    continue;
                }
                freed += CleanFolderContents(d.FullName, ct);
                try { d.Delete(false); } catch { }
            }
        }
        catch { }
        return freed;
    }

    // ---------- Ölçüm ----------
    public static (long size, long count) MeasureFolder(string dir, CancellationToken ct)
    {
        long size = 0, count = 0;
        if (!Directory.Exists(dir)) return (0, 0);
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
        };
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            {
                if ((count & 1023) == 0 && ct.IsCancellationRequested) break;
                size += f.Length; count++;
            }
        }
        catch { }
        return (size, count);
    }

    static IEnumerable<string> SafeEnum(string dir, bool recursive, bool files)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = recursive, AttributesToSkip = 0 };
        try { return files ? Directory.EnumerateFiles(dir, "*", opts) : Directory.EnumerateDirectories(dir, "*", opts); }
        catch { return Array.Empty<string>(); }
    }

    public static List<TempLocation> TempLocations()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var list = new List<TempLocation>
        {
            new() { Title = "Kullanıcı geçici dosyaları", Path = Path.GetTempPath().TrimEnd('\\') },
            new() { Title = "Windows geçici dosyaları", Path = Path.Combine(win, "Temp") },
            new() { Title = "Windows Update indirme önbelleği", Path = Path.Combine(win, @"SoftwareDistribution\Download") },
            new() { Title = "Teslim İyileştirme önbelleği", Path = Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") },
            new() { Title = "Windows hata raporları", Path = Path.Combine(progData, @"Microsoft\Windows\WER\ReportArchive") },
            new() { Title = "Windows hata raporu kuyruğu", Path = Path.Combine(progData, @"Microsoft\Windows\WER\ReportQueue") },
            new() { Title = "Uygulama çökme dökümleri", Path = Path.Combine(local, "CrashDumps") },
            new() { Title = "DirectX gölgelendirici önbelleği", Path = Path.Combine(local, "D3DSCache") },
            new() { Title = "NVIDIA gölgelendirici önbelleği", Path = Path.Combine(local, @"NVIDIA\DXCache") },
        };
        return list.Where(l => Directory.Exists(l.Path)).ToList();
    }

    // ---------- Güvenlik ----------
    static readonly string[] Protected =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Native.GetDownloadsFolder(),
    };

    /// <summary>Silinmesi sistemi bozacak yolları reddeder. Sorun yoksa null döner.</summary>
    public static string WhyBlocked(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Geçersiz yol.";
        var p = path.TrimEnd('\\');
        if (p.Length <= 2) return "Bir sürücünün tamamı silinemez.";
        foreach (var pr in Protected)
            if (!string.IsNullOrEmpty(pr) && p.Equals(pr.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return "Bu bir sistem / kullanıcı ana klasörü; tamamen silinemez (içindekileri seçebilirsiniz).";
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        if (p.StartsWith(win, StringComparison.OrdinalIgnoreCase) &&
            !p.StartsWith(win + "Temp", StringComparison.OrdinalIgnoreCase) &&
            !p.StartsWith(win + @"SoftwareDistribution\Download", StringComparison.OrdinalIgnoreCase))
            return "Windows klasörünün içi elle silinemez (Windows'u bozar). Temizlik sekmesindeki güvenli seçenekleri kullanın.";
        var name = Path.GetFileName(p);
        var parent = Path.GetDirectoryName(p);
        if (parent != null && parent.TrimEnd('\\').Length <= 2)
        {
            if (name.StartsWith('$') || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Recovery", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Boot", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("bootmgr", StringComparison.OrdinalIgnoreCase))
                return "Bu bir Windows sistem öğesi; silinemez.";
        }
        return null;
    }

    /// <summary>Boş dosya / boş klasör aramasında atlanacak klasörler.</summary>
    public static HashSet<int> ExcludedRoots(FileIndex idx, bool includeAppData)
    {
        var set = new HashSet<int>();
        void Add(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            int i = idx.Find(path);
            if (i >= 0) set.Add(i);
        }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        string sys = idx.RootPath;
        foreach (var n in new[] { "$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent", "PerfLogs", "$Windows.~WS", "$Windows.~BT", "$SysReset" })
            Add(Path.Combine(sys, n));
        if (!includeAppData)
        {
            int users = idx.Find(Path.Combine(sys, "Users"));
            if (users >= 0)
                foreach (var u in idx.Children(users))
                {
                    if (!idx.IsDir(u)) continue;
                    foreach (var c in idx.Children(u))
                        if (idx.IsDir(c) && idx.NameSpan(c).Equals("AppData", StringComparison.OrdinalIgnoreCase)) set.Add(c);
                }
            // Kullanıcı dışı gizli klasörler (.git, node_modules vb. içindeki işaret dosyaları önemlidir)
        }
        return set;
    }

    static bool UnderExcluded(FileIndex idx, int i, HashSet<int> excl)
    {
        for (int c = i; c >= 0; c = idx.Parent[c])
        {
            if (excl.Contains(c)) return true;
            var n = idx.NameSpan(c);
            if (c != idx.Root && n.Length > 0 && (n[0] == '$' || n.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || n.Equals("node_modules", StringComparison.OrdinalIgnoreCase))) return true;
            if (c == idx.Root) break;
        }
        return false;
    }

    const uint AttrHiddenSystem = 0x2 | 0x4;
    const uint AttrReparse = 0x400;

    public static List<int> FindEmptyFiles(FileIndex idx, bool includeAppData, CancellationToken ct)
    {
        var excl = ExcludedRoots(idx, includeAppData);
        var res = new List<int>();
        for (int i = 0; i < idx.Count; i++)
        {
            if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
            byte f = idx.Flags[i];
            if ((f & (FileIndex.FDir | FileIndex.FDeleted)) != 0 || idx.Size[i] != 0) continue;
            if ((idx.Attr[i] & (AttrHiddenSystem | AttrReparse)) != 0) continue;
            if (UnderExcluded(idx, i, excl)) continue;
            res.Add(i);
        }
        return res;
    }

    /// <summary>İçinde hiç dosya olmayan en üst klasörler (alt klasörleri de boşsa).</summary>
    public static List<int> FindEmptyFolders(FileIndex idx, bool includeAppData, CancellationToken ct)
    {
        var excl = ExcludedRoots(idx, includeAppData);
        var res = new List<int>();
        for (int i = 0; i < idx.Count; i++)
        {
            if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
            byte f = idx.Flags[i];
            if ((f & FileIndex.FDir) == 0 || (f & FileIndex.FDeleted) != 0 || i == idx.Root) continue;
            if (idx.FileCount[i] != 0) continue;
            if ((idx.Attr[i] & (AttrHiddenSystem | AttrReparse)) != 0) continue;
            int p = idx.Parent[i];
            if (p >= 0 && p != idx.Root && idx.FileCount[p] == 0 && (idx.Attr[p] & (AttrHiddenSystem | AttrReparse)) == 0
                && !excl.Contains(p)) continue; // üst klasör de boş: onu listeleyeceğiz
            if (UnderExcluded(idx, i, excl)) continue;
            if (Cleanup.WhyBlocked(idx.GetPath(i)) != null) continue;
            res.Add(i);
        }
        return res;
    }
}

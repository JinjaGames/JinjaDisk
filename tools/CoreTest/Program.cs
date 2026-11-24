using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using JinjaDisk.Core;

// Kullanım: CoreTest <kök> [çıktı.txt]
//   kök = "C:\" (MFT, yönetici gerekir) ya da bir klasör (klasör taraması)
var root = args.Length > 0 ? args[0] : @"C:\";
var outPath = args.Length > 1 ? args[1] : null;
var w = outPath != null ? new StreamWriter(outPath) { AutoFlush = true } : Console.Out;
try
{
    w.WriteLine($"Yönetici: {Native.IsElevated}");
    var sw = Stopwatch.StartNew();
    var idx = Scanner.Scan(root, null, CancellationToken.None, out var note);
    w.WriteLine($"Tarama: {sw.Elapsed.TotalSeconds:0.00} sn  MFT={idx.UsedMft}  not={note}");
    w.WriteLine($"Kayıt={idx.Count:N0} dosya={idx.FileTotal:N0} klasör={idx.DirCount:N0}");
    w.WriteLine($"Kök boyut={Fmt.Size(idx.Size[idx.Root])} diskte={Fmt.Size(idx.Alloc[idx.Root])}");
    if (root.Length <= 3)
    {
        var d = new DriveInfo(root);
        w.WriteLine($"Sürücü dolu alan={Fmt.Size(d.TotalSize - d.TotalFreeSpace)}");
    }
    w.WriteLine("-- Kökteki en büyük 12 --");
    foreach (var c in idx.ChildrenBySize(idx.Root).Take(12))
        w.WriteLine($"  {Fmt.Size(idx.Alloc[c]),10}  {idx.FileCount[c],9:N0}  {idx.GetPath(c)}");

    // Yol doğrulama: rastgele dosyaların gerçekten var olup olmadığı
    var rnd = new Random(1);
    int checkedN = 0, exist = 0, sizeOk = 0;
    for (int k = 0; k < 4000 && checkedN < 400; k++)
    {
        int i = rnd.Next(idx.Count);
        if (idx.IsDir(i)) continue;
        var p = idx.GetPath(i);
        if (p.Contains("$")) continue;
        checkedN++;
        try
        {
            var fi = new FileInfo(p);
            if (fi.Exists) { exist++; if (fi.Length == idx.Size[i]) sizeOk++; }
        }
        catch { }
    }
    w.WriteLine($"Doğrulama: {checkedN} rastgele dosya, var={exist}, boyut eşleşen={sizeOk}");

    sw.Restart();
    var st = DiskStats.Compute(idx);
    w.WriteLine($"İstatistik: {sw.ElapsedMilliseconds} ms, uzantı={st.Exts.Count}");
    for (int c = 0; c < 10; c++) w.WriteLine($"  {Categories.Names[c],-14} {Fmt.Size(st.CatSize[c]),10} {st.CatCount[c],10:N0}");
    w.WriteLine("-- En büyük 5 dosya --");
    foreach (var i in st.TopFiles.Take(5)) w.WriteLine($"  {Fmt.Size(idx.Alloc[i]),10}  {idx.GetPath(i)}");

    foreach (var q in new[] { "notepad", "*.exe", "ext:mp4;mkv size:>100mb", "\\Downloads\\ ext:zip", "folder: node_modules", "!dll system32" })
    {
        sw.Restart();
        var r = SearchQuery.Parse(q).Run(idx, CancellationToken.None);
        long t1 = sw.ElapsedMilliseconds;
        sw.Restart();
        ResultSorter.Sort(idx, r, SortKey.Name, false, CancellationToken.None);
        w.WriteLine($"Ara '{q}': {r.Length:N0} sonuç, {t1} ms (+{sw.ElapsedMilliseconds} ms sıralama)  ilk: {(r.Length > 0 ? idx.GetPath(r[0]) : "-")}");
    }

    sw.Restart();
    var ef = Cleanup.FindEmptyFiles(idx, false, CancellationToken.None);
    var ed = Cleanup.FindEmptyFolders(idx, false, CancellationToken.None);
    w.WriteLine($"Boş dosya={ef.Count:N0} boş klasör={ed.Count:N0} ({sw.ElapsedMilliseconds} ms)");
    foreach (var i in ed.Take(5)) w.WriteLine("  boş klasör: " + idx.GetPath(i));
    foreach (var i in ef.Take(5)) w.WriteLine("  boş dosya: " + idx.GetPath(i));
    var rb = Cleanup.QueryRecycleBin();
    w.WriteLine($"Geri dönüşüm kutusu: {Fmt.Size(rb.size)} / {rb.count} öğe");
    w.WriteLine($"Bellek: {GC.GetTotalMemory(true) / 1048576} MB");
}
catch (Exception ex) { w.WriteLine("HATA: " + ex); }
w.Flush();

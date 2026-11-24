using System;
using System.Collections.Generic;
using System.Globalization;

namespace JinjaDisk.Core;

public enum Category { Video, Image, Audio, Document, Archive, Game, Program, Code, System, Other }

public static class Categories
{
    public static readonly string[] Names =
        { "Video", "Görsel", "Ses", "Belge", "Arşiv / İmaj", "Oyun verisi", "Program", "Geliştirme", "Sistem", "Diğer" };

    // Kategori renkleri (koyu tema üstünde okunaklı, birbirinden ayrışan tonlar)
    public static readonly uint[] Colors =
    {
        0xFFFF5D73, // video - mercan
        0xFF4FD1C5, // görsel - turkuaz
        0xFFF6AD55, // ses - turuncu
        0xFF63B3ED, // belge - mavi
        0xFFB794F4, // arşiv - lila
        0xFF68D391, // oyun - yeşil
        0xFFF687B3, // program - pembe
        0xFFFAF089, // geliştirme - sarı
        0xFF8A94A8, // sistem - gri
        0xFF5A6478, // diğer - koyu gri
    };

    public static readonly string[] Glyphs =
    {
        "", "", "", "", "", "", "", "", "", ""
    };

    static readonly Dictionary<string, Category> map = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Category>.AlternateLookup<ReadOnlySpan<char>> lookup;

    static void Add(Category c, string list)
    {
        foreach (var e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map[e] = c;
    }

    static Categories()
    {
        Add(Category.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts mts vob 3gp ogv rmvb divx");
        Add(Category.Image, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd svg ico tga dds exr hdr avif jxl");
        Add(Category.Audio, "mp3 wav flac aac ogg m4a wma opus aiff aif mid midi ape alac");
        Add(Category.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf md csv epub mobi xps one pub");
        Add(Category.Archive, "zip rar 7z tar gz tgz bz2 xz zst cab iso img vhd vhdx vmdk vdi qcow2 wim esd dmg");
        Add(Category.Game, "pak vpk bundle assets ucas utoc bsa ba2 forge bin1 rpf big wad gcf ytd res resource bnk wem pck arc dat2 cpk fbx blend unitypackage uasset umap");
        Add(Category.Program, "exe dll msi msix appx sys2 ocx cpl scr com bat cmd ps1 jar apk xapk aab ipa node so dylib");
        Add(Category.Code, "cs cpp c h hpp js ts jsx tsx py java kt go rs dart swift php rb html css scss json xml yaml yml sql lock pdb obj o lib a class pyc whl nupkg gradle sln csproj dill pack idx ilk ipch");
        Add(Category.System, "sys cab_ log etl evtx tmp temp cache dmp mdmp edb db-wal db-shm regtrans-ms blf msp mui cat manifest");
        lookup = map.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public static Category Of(FileIndex idx, int i)
    {
        var name = idx.NameSpan(i);
        if (name.Length > 0 && name[0] == '$') return Category.System;
        var ext = idx.ExtSpan(i);
        if (ext.IsEmpty) return Category.Other;
        if (lookup.TryGetValue(ext, out var c)) return c;
        return Category.Other;
    }

    /// <summary>Bir kategorinin arama sorgusu (ext:a;b;c). "Diğer" için null.</summary>
    public static string ExtQuery(Category c)
    {
        var list = new List<string>();
        foreach (var kv in map) if (kv.Value == c) list.Add(kv.Key);
        return list.Count == 0 ? null : "ext:" + string.Join(";", list);
    }

    public static Category OfExt(string ext) =>
        !string.IsNullOrEmpty(ext) && map.TryGetValue(ext, out var c) ? c : Category.Other;
}

public static class Fmt
{
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Size(long b)
    {
        if (b < 0) b = 0;
        if (b < 1024) return b + " B";
        double v = b;
        string[] u = { "KB", "MB", "GB", "TB", "PB" };
        int k = -1;
        do { v /= 1024; k++; } while (v >= 1024 && k < u.Length - 1);
        return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", Tr) + " " + u[k];
    }

    public static string Num(long n) => n.ToString("N0", Tr);

    public static string Date(long fileTime)
    {
        if (fileTime <= 0) return "";
        try { return DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("dd.MM.yyyy HH:mm", Tr); }
        catch { return ""; }
    }

    public static string Percent(double f) => (f * 100).ToString(f >= 0.1 ? "0" : "0.0", Tr) + "%";
}

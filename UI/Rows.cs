using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using JinjaDisk.Core;

namespace JinjaDisk.UI;

public static class Brushes2
{
    static readonly Brush[] cat = new Brush[10];
    public static readonly Brush Folder;

    static Brushes2()
    {
        for (int i = 0; i < 10; i++)
        {
            var b = new SolidColorBrush(TreemapControl.ToColor(Categories.Colors[i]));
            b.Freeze();
            cat[i] = b;
        }
        Folder = new SolidColorBrush(Color.FromRgb(0xF6, 0xC3, 0x5B));
        Folder.Freeze();
    }

    public static Brush Cat(Category c) => cat[(int)c];
}

public abstract class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void Raise([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Arama sonucu satırı (görünür olduğunda tembel oluşturulur).</summary>
public sealed class FileRow
{
    readonly FileIndex idx;
    public readonly int Index;
    public readonly int Position;
    string folder;

    public FileRow(FileIndex idx, int index, int position)
    {
        this.idx = idx; Index = index; Position = position;
        bool dir = idx.IsDir(index);
        Name = idx.Name(index);
        SizeText = Fmt.Size(idx.Size[index]);
        DateText = Fmt.Date(idx.Mtime[index]);
        if (dir) { Glyph = ""; IconBrush = Brushes2.Folder; }
        else
        {
            var c = Categories.Of(idx, index);
            Glyph = Categories.Glyphs[(int)c];
            IconBrush = Brushes2.Cat(c);
        }
    }

    public string Name { get; }
    public string Folder => folder ??= idx.GetParentPath(Index);
    public string SizeText { get; }
    public string DateText { get; }
    public string Glyph { get; }
    public Brush IconBrush { get; }
}

/// <summary>Milyonlarca sonucu kopyalamadan ListBox'a veren sanal liste.</summary>
public sealed class VirtualRowList : IList
{
    readonly FileIndex idx;
    readonly int[] items;
    readonly FileRow[] cache;

    public VirtualRowList(FileIndex idx, int[] items)
    {
        this.idx = idx; this.items = items;
        cache = new FileRow[items.Length];
    }

    public int[] Items => items;
    public int Count => items.Length;
    public object this[int i]
    {
        get => cache[i] ??= new FileRow(idx, items[i], i);
        set => throw new NotSupportedException();
    }
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public object SyncRoot => this;
    public bool IsSynchronized => false;
    public int IndexOf(object value) => value is FileRow r && r.Position < cache.Length && cache[r.Position] == r ? r.Position : -1;
    public bool Contains(object value) => IndexOf(value) >= 0;
    public IEnumerator GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    public void CopyTo(Array array, int index) { for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i); }
    public int Add(object value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object value) => throw new NotSupportedException();
    public void Remove(object value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}

/// <summary>Disk haritasındaki düz (girintili) ağaç satırı.</summary>
public sealed class TreeRow : Notify
{
    readonly FileIndex idx;
    bool expanded;
    public readonly int Index;
    public readonly int Depth;
    public readonly bool IsDir;

    public TreeRow(FileIndex idx, int index, int depth, long parentAlloc)
    {
        this.idx = idx; Index = index; Depth = depth;
        IsDir = idx.IsDir(index);
        Name = idx.DisplayName(index);
        HasChildren = IsDir && idx.HasChildren(index);
        if (IsDir) { Glyph = ""; IconBrush = Brushes2.Folder; }
        else
        {
            var c = Categories.Of(idx, index);
            Glyph = Categories.Glyphs[(int)c];
            IconBrush = Brushes2.Cat(c);
        }
        Recalc(parentAlloc);
    }

    public void Recalc(long parentAlloc)
    {
        Fraction = parentAlloc > 0 ? Math.Clamp((double)idx.Alloc[Index] / parentAlloc, 0, 1) : (Depth == 0 ? 1 : 0);
        SizeText = Fmt.Size(idx.Alloc[Index]);
        FilesText = IsDir ? Fmt.Num(idx.FileCount[Index]) : "";
        DateText = Fmt.Date(idx.Mtime[Index]);
        Raise(nameof(SizeText)); Raise(nameof(FilesText)); Raise(nameof(PercentText)); Raise(nameof(BarWidth));
    }

    public string Name { get; }
    public string Glyph { get; }
    public Brush IconBrush { get; }
    public bool HasChildren { get; }
    public double Fraction { get; private set; }
    public string SizeText { get; private set; }
    public string FilesText { get; private set; }
    public string DateText { get; private set; }
    public string PercentText => Fmt.Percent(Fraction);
    public double BarWidth => Math.Max(Fraction > 0 ? 2 : 0, Fraction * 70);
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    public Visibility ChevronVisibility => HasChildren ? Visibility.Visible : Visibility.Hidden;
    public string Chevron => expanded ? "" : "";
    public bool IsExpanded
    {
        get => expanded;
        set { expanded = value; Raise(); Raise(nameof(Chevron)); }
    }
}

/// <summary>Toplu ekleme/çıkarmada tek bir Reset bildirimi yapan koleksiyon.</summary>
public sealed class BulkCollection<T> : ObservableCollection<T>
{
    public void InsertRange(int at, IList<T> items)
    {
        CheckReentrancy();
        ((List<T>)Items).InsertRange(at, items);
        Notify();
    }

    public void RemoveRange(int at, int count)
    {
        CheckReentrancy();
        ((List<T>)Items).RemoveRange(at, count);
        Notify();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        ((List<T>)Items).AddRange(items);
        Notify();
    }

    void Notify()
    {
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>Temizlik listesindeki işaretlenebilir öğe.</summary>
public sealed class CleanItem : Notify
{
    bool isChecked;
    public string Path { get; init; }
    public string Name { get; init; }
    public string Folder { get; init; }
    public long Size { get; set; }
    public long Mtime { get; init; }
    public CleanKind Kind { get; init; }
    public int Index { get; init; } = -1;
    public string Glyph { get; init; } = "";
    public Brush IconBrush { get; init; } = Brushes.Gray;
    public string SizeText => Fmt.Size(Size);
    public string DateText => Fmt.Date(Mtime);
    public bool IsChecked
    {
        get => isChecked;
        set { if (isChecked == value) return; isChecked = value; Raise(); CheckedChanged?.Invoke(this); }
    }
    public static event Action<CleanItem> CheckedChanged;
}

public sealed class SimpleBar
{
    public string Title { get; init; }
    public string Sub { get; init; }
    public string Value { get; init; }
    public double Fraction { get; init; }
    public Brush Color { get; init; }
    public string Glyph { get; init; }
    public int Index { get; init; } = -1;
    public string Tag { get; init; }
    public string Key { get; init; }
    public GridLength BarStar => new(Math.Max(0.0001, Fraction), GridUnitType.Star);
    public GridLength RestStar => new(Math.Max(0.0001, 1 - Fraction), GridUnitType.Star);
}

public sealed class DriveCard
{
    public string Root { get; init; }
    public string Title { get; init; }
    public string Sub { get; init; }
    public double Fraction { get; init; }
    public string UsedText { get; init; }
    public string FreeText { get; init; }
    public bool LowSpace => Fraction > 0.85;
    public Brush BarBrush => (Brush)Application.Current.FindResource(LowSpace ? "DangerGradient" : "BarGradient");
    public override string ToString() => Title;
    public GridLength BarStar => new(Math.Max(0.0001, Fraction), GridUnitType.Star);
    public GridLength RestStar => new(Math.Max(0.0001, 1 - Fraction), GridUnitType.Star);
}

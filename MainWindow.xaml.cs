using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using JinjaDisk.Core;
using JinjaDisk.UI;

namespace JinjaDisk;

public partial class MainWindow : Window
{
    FileIndex idx;
    DiskStats stats;
    CancellationTokenSource scanCts, searchCts, detailCts;
    readonly BulkCollection<TreeRow> treeRows = new();
    readonly BulkCollection<CleanItem> detailItems = new();
    readonly DispatcherTimer searchTimer;
    SortKey sortKey = SortKey.Name;
    bool sortDesc, userSorted, suppressMapSync, suppressCheck;
    List<TempLocation> tempLocs;

    enum DetailMode { None, Temp, Downloads, EmptyFiles, EmptyDirs, BigFiles }
    DetailMode detailMode;

    public MainWindow()
    {
        InitializeComponent();
        TreeList.ItemsSource = treeRows;
        DetailList.ItemsSource = detailItems;
        CleanItem.CheckedChanged += _ => { if (!suppressCheck) UpdateDetailSelection(); };

        searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RunSearch(); };

        Map.ItemHovered += Map_Hovered;
        Map.ItemClicked += Map_Clicked;
        Map.ItemOpened += Map_Opened;

        Legend.ItemsSource = Enumerable.Range(0, 10).Select(i => new SimpleBar
        {
            Title = Categories.Names[i], Color = Brushes2.Cat((Category)i)
        }).ToList();

        StateChanged += (_, _) => ApplyWindowState();
        Loaded += OnLoaded;
        Closing += (_, _) => { scanCts?.Cancel(); searchCts?.Cancel(); detailCts?.Cancel(); };
    }

    // =====================================================================
    // Pencere
    // =====================================================================
    void ApplyWindowState()
    {
        if (WindowState == WindowState.Maximized)
        {
            var t = SystemParameters.WindowResizeBorderThickness;
            RootBorder.Margin = new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4);
            MaxBtn.Content = "";
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            MaxBtn.Content = "";
        }
    }

    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object s, RoutedEventArgs e) => Close();

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadDrives();
        RefreshRecycle();
        UpdateModeBadge(null);
        _ = MeasureDownloadsCard();
        _ = MeasureTempCard();
        var args = Environment.GetCommandLineArgs();
        int shot = Array.IndexOf(args, "--shot");
        int rootArg = Array.IndexOf(args, "--root");
        string startRoot = rootArg > 0 && rootArg + 1 < args.Length ? args[rootArg + 1]
            : Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        // Açılışta sistem sürücüsünü otomatik tara
        await StartScan(startRoot);
        if (shot > 0 && shot + 1 < args.Length) await TakeShots(args[shot + 1]);
    }

    /// <summary>Test: her sayfanın ekran görüntüsünü PNG olarak kaydeder ve kapanır.</summary>
    async Task TakeShots(string dir)
    {
        Directory.CreateDirectory(dir);
        async Task Snap(string name)
        {
            await Task.Delay(700);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var dpi = VisualTreeHelper.GetDpi(this);
            var el = (FrameworkElement)Content;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(el);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        await Snap("1-overview");
        SearchFor("ext:zip;rar;iso size:>500mb");
        await Task.Delay(400);
        await Snap("2-search");
        NavMap.IsChecked = true;
        if (treeRows.Count > 1) { TreeList.SelectedIndex = 0; }
        await Snap("3-map");
        NavTypes.IsChecked = true;
        await Snap("4-types");
        NavClean.IsChecked = true;
        await Task.Delay(300);
        EmptyDirs_Click(null, null);
        await Task.Delay(1500);
        await Snap("5-clean");
        Close();
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageOverview == null) return;
        PageOverview.Visibility = NavOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSearch.Visibility = NavSearch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageMap.Visibility = NavMap.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageTypes.Visibility = NavTypes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageClean.Visibility = NavClean.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavSearch.IsChecked == true)
            Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
        if (NavClean.IsChecked == true) RefreshRecycle();
    }

    // =====================================================================
    // Sürücüler ve tarama
    // =====================================================================
    void LoadDrives()
    {
        var cards = new List<DriveCard>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
                long total = d.TotalSize, free = d.TotalFreeSpace, used = total - free;
                string letter = d.Name.TrimEnd('\\');
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "Çıkarılabilir Disk" : "Yerel Disk") : d.VolumeLabel;
                cards.Add(new DriveCard
                {
                    Root = d.Name,
                    Title = $"{label} ({letter})",
                    Sub = $"{d.DriveFormat} · {Fmt.Size(total)}",
                    Fraction = total > 0 ? (double)used / total : 0,
                    UsedText = $"{Fmt.Size(used)} dolu",
                    FreeText = $"{Fmt.Size(free)} boş"
                });
            }
            catch { }
        }
        DrivesList.ItemsSource = cards;
        DriveCombo.ItemsSource = cards;
        var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        DriveCombo.SelectedItem = cards.FirstOrDefault(c => c.Root.Equals(sys, StringComparison.OrdinalIgnoreCase)) ?? cards.FirstOrDefault();
    }

    async void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (scanCts != null) { scanCts.Cancel(); return; }
        if (DriveCombo.SelectedItem is DriveCard d) await StartScan(d.Root);
    }

    async void DriveScan_Click(object sender, RoutedEventArgs e)
    {
        if (scanCts != null) return;
        if (sender is Button b && b.Tag is string root)
        {
            DriveCombo.SelectedItem = ((List<DriveCard>)DriveCombo.ItemsSource).FirstOrDefault(c => c.Root == root);
            await StartScan(root);
        }
    }

    async void FolderScan_Click(object sender, RoutedEventArgs e)
    {
        if (scanCts != null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Taranacak klasörü seç" };
        if (dlg.ShowDialog(this) == true) await StartScan(dlg.FolderName);
    }

    void SetScanning(bool on)
    {
        ScanBtnText.Text = on ? "Durdur" : "Tara";
        ScanBtnIcon.Text = on ? "" : "";
        ScanProgressBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.IsIndeterminate = on;
        ScanProgressBar.Value = 0;
    }

    async Task StartScan(string root)
    {
        if (scanCts != null) return;
        scanCts = new CancellationTokenSource();
        var ct = scanCts.Token;
        SetScanning(true);
        StatusText.Text = "Tarama başlıyor…";
        var prog = new Progress<ScanProgress>(p =>
        {
            if (scanCts == null) return;
            StatusText.Text = p.Phase + (p.Items > 0 ? $" · {Fmt.Num(p.Items)} kayıt" : "");
            if (p.Fraction >= 0) { ScanProgressBar.IsIndeterminate = false; ScanProgressBar.Value = p.Fraction; }
            else ScanProgressBar.IsIndeterminate = true;
        });
        try
        {
            var (newIdx, note) = await Task.Run(() =>
            {
                var r = Scanner.Scan(root, prog, ct, out var n);
                return (r, n);
            }, ct);
            StatusText.Text = "İstatistikler hesaplanıyor…";
            var st = await Task.Run(() => DiskStats.Compute(newIdx), ct);
            idx = newIdx;
            stats = st;
            OnIndexReady(note);
            GC.Collect(2, GCCollectionMode.Optimized, false);
        }
        catch (OperationCanceledException) { StatusText.Text = "Tarama durduruldu."; }
        catch (Exception ex) { StatusText.Text = "Hata: " + ex.Message; }
        finally
        {
            scanCts?.Dispose();
            scanCts = null;
            SetScanning(false);
        }
    }

    void UpdateModeBadge(FileIndex i)
    {
        bool fast = i?.UsedMft ?? Native.IsElevated;
        ModeBadge.Background = new SolidColorBrush(fast ? Color.FromRgb(0x16, 0x30, 0x2A) : Color.FromRgb(0x3A, 0x2C, 0x16));
        var fg = (Brush)FindResource(fast ? "Success" : "Warn");
        ModeBadgeIcon.Foreground = fg; ModeBadgeText.Foreground = fg;
        ModeBadgeIcon.Text = fast ? "" : "";
        ModeBadgeText.Text = i == null
            ? (fast ? "Hızlı MFT modu hazır" : "Yönetici değil: yavaş mod")
            : (fast ? "MFT'den okundu" : "Klasör taraması");
    }

    void OnIndexReady(string note)
    {
        StatusText.Text = $"{Fmt.Num(idx.FileTotal)} dosya, {Fmt.Num(idx.DirCount)} klasör · {idx.ScanTime.TotalSeconds:0.0} sn"
                          + (note != null ? "\n" + note : "");
        UpdateModeBadge(idx);
        UpdateTitleInfo();
        PopulateOverview();
        PopulateTree();
        PopulateTypes();
        Map.SetRoot(idx, idx.Root);
        UpdateMapHeader();
        MapEmpty.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) RunSearch();
        else SearchEmptyText.Text = "Aramak için yazmaya başla";
        _ = ComputeCleanCounts();
        LoadDrives();
    }

    void UpdateTitleInfo()
    {
        if (idx == null) return;
        TitleInfo.Text = $"{idx.RootPath}   ·   {Fmt.Size(idx.Alloc[idx.Root])}   ·   {Fmt.Num(idx.FileTotal)} dosya";
    }

    // =====================================================================
    // Genel bakış
    // =====================================================================
    void PopulateOverview()
    {
        OverviewEmpty.Visibility = Visibility.Collapsed;
        OverviewScanned.Visibility = Visibility.Visible;
        int root = idx.Root;
        long rootAlloc = Math.Max(1, idx.Alloc[root]);

        StatFiles.Text = Fmt.Num(idx.FileTotal);
        StatDirs.Text = Fmt.Num(idx.DirCount);
        StatSize.Text = Fmt.Size(idx.Alloc[root]);
        StatSizeSub.Text = "";
        try
        {
            if (idx.RootPath.Length == 3)
            {
                var d = new DriveInfo(idx.RootPath);
                long used = d.TotalSize - d.TotalFreeSpace;
                long hidden = used - idx.Alloc[root];
                StatSizeSub.Text = hidden > (1L << 30)
                    ? $"+{Fmt.Size(hidden)} sistem/gölge kopya (görünmez)"
                    : $"Dolu alanın tamamı";
            }
            else StatSizeSub.Text = idx.RootPath;
        }
        catch { }
        StatTime.Text = idx.ScanTime.TotalSeconds.ToString("0.0", Fmt.Tr) + " sn";
        StatTimeSub.Text = idx.UsedMft ? "MFT doğrudan okundu" : "Klasörler tek tek gezildi";

        // Kategori halkası + çubuklar
        long total = Math.Max(1, stats.TotalAlloc);
        var order = Enumerable.Range(0, 10).Where(c => stats.CatSize[c] > 0).OrderByDescending(c => stats.CatSize[c]).ToList();
        Donut.Slices.Clear();
        foreach (var c in order) Donut.Slices.Add((stats.CatSize[c], TreemapControl.ToColor(Categories.Colors[c])));
        Donut.CenterTitle = Fmt.Size(stats.TotalAlloc);
        Donut.CenterSub = "dosyalarda";
        Donut.Update();
        CategoryBars.ItemsSource = order.Select(c => new SimpleBar
        {
            Title = Categories.Names[c],
            Sub = Fmt.Num(stats.CatCount[c]) + " dosya",
            Value = Fmt.Size(stats.CatSize[c]),
            Fraction = (double)stats.CatSize[c] / total,
            Color = Brushes2.Cat((Category)c),
            Glyph = Categories.Glyphs[c],
            Key = "cat:" + c
        }).ToList();

        // Kökteki en büyük klasörler
        var folderBrush = (Brush)FindResource("BarGradient");
        TopFoldersList.ItemsSource = idx.ChildrenBySize(root).Take(9).Select(k => new SimpleBar
        {
            Title = idx.Name(k),
            Sub = idx.IsDir(k) ? Fmt.Num(idx.FileCount[k]) + " dosya" : "dosya",
            Value = Fmt.Size(idx.Alloc[k]),
            Fraction = (double)idx.Alloc[k] / rootAlloc,
            Color = idx.IsDir(k) ? folderBrush : Brushes2.Cat(Categories.Of(idx, k)),
            Glyph = idx.IsDir(k) ? "" : Categories.Glyphs[(int)Categories.Of(idx, k)],
            Index = k
        }).ToList();

        // En büyük dosyalar
        var live = stats.TopFiles.Where(i => !idx.IsDeleted(i)).Take(15).ToList();
        long maxF = live.Count > 0 ? Math.Max(1, idx.Alloc[live[0]]) : 1;
        TopFilesList.ItemsSource = live.Select(k =>
        {
            var c = Categories.Of(idx, k);
            return new SimpleBar
            {
                Title = idx.Name(k),
                Sub = idx.GetParentPath(k),
                Value = Fmt.Size(idx.Alloc[k]),
                Fraction = (double)idx.Alloc[k] / maxF,
                Color = Brushes2.Cat(c),
                Glyph = Categories.Glyphs[(int)c],
                Index = k
            };
        }).ToList();
    }

    void Bar_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not SimpleBar b) return;
        if (b.Index >= 0 && idx != null) { ShowInMap(b.Index); return; }
        ActivateKey(b.Key);
    }

    void ActivateKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (key.StartsWith("cat:"))
        {
            var c = (Category)int.Parse(key[4..]);
            var q = Categories.ExtQuery(c);
            if (q != null) SearchFor(q);
            else NavTypes.IsChecked = true;
        }
        else if (key.StartsWith("ext:")) SearchFor(key);
    }

    void ShowBigFiles_Click(object sender, RoutedEventArgs e) => SearchFor("file: size:>100mb");

    // =====================================================================
    // Arama
    // =====================================================================
    void SearchFor(string query)
    {
        sortKey = SortKey.Size; sortDesc = true; userSorted = true;
        UpdateSortHeaders();
        NavSearch.IsChecked = true;
        SearchBox.Text = query;
        SearchBox.CaretIndex = query.Length;
        searchTimer.Stop();
        RunSearch();
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        searchTimer.Stop();
        searchTimer.Start();
    }

    void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Down || e.Key == Key.Enter) && SearchList.Items.Count > 0)
        {
            SearchList.SelectedIndex = 0;
            SearchList.UpdateLayout();
            (SearchList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
    }

    void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string t) return;
        var cur = SearchBox.Text.Trim();
        SearchBox.Text = (cur.Length > 0 ? cur + " " : "") + t;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    void ChipClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    async void RunSearch()
    {
        searchCts?.Cancel();
        if (idx == null)
        {
            SearchEmpty.Visibility = Visibility.Visible;
            SearchEmptyText.Text = "Önce bir sürücü tara";
            return;
        }
        var text = SearchBox.Text;
        var q = SearchQuery.Parse(text);
        if (q.IsEmpty)
        {
            SearchList.ItemsSource = null;
            ResultCount.Text = "";
            SearchEmpty.Visibility = Visibility.Visible;
            SearchEmptyText.Text = "Aramak için yazmaya başla";
            return;
        }
        var cts = searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        var index = idx;
        var key = sortKey; var desc = sortDesc; bool sort = userSorted;
        var sw = Stopwatch.StartNew();
        int[] res;
        bool sorted;
        try
        {
            (res, sorted) = await Task.Run(() =>
            {
                var r = q.Run(index, ct);
                bool s = sort || r.Length <= 250_000;
                if (s) ResultSorter.Sort(index, r, key, desc, ct);
                return (r, s);
            }, ct);
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || index != idx) return;
        SearchList.ItemsSource = new VirtualRowList(index, res);
        ResultCount.Text = $"{Fmt.Num(res.Length)} sonuç · {sw.ElapsedMilliseconds} ms" + (sorted ? "" : " · sıralamak için başlığa tıkla");
        SearchEmpty.Visibility = res.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchEmptyText.Text = "Sonuç yok";
    }

    async void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string t) return;
        var k = Enum.Parse<SortKey>(t);
        if (k == sortKey && userSorted) sortDesc = !sortDesc;
        else { sortKey = k; sortDesc = k is SortKey.Size or SortKey.Date; }
        userSorted = true;
        UpdateSortHeaders();
        if (SearchList.ItemsSource is VirtualRowList v && idx != null)
        {
            var arr = v.Items;
            var index = idx; var key = sortKey; var desc = sortDesc;
            ResultCount.Text = "Sıralanıyor…";
            await Task.Run(() => ResultSorter.Sort(index, arr, key, desc, CancellationToken.None));
            if (index != idx) return;
            SearchList.ItemsSource = new VirtualRowList(index, arr);
            ResultCount.Text = $"{Fmt.Num(arr.Length)} sonuç";
        }
    }

    void UpdateSortHeaders()
    {
        string arrow = sortDesc ? "  ↓" : "  ↑";
        HdrName.Content = "Ad" + (sortKey == SortKey.Name ? arrow : "");
        HdrFolder.Content = "Klasör" + (sortKey == SortKey.Folder ? arrow : "");
        HdrSize.Content = "Boyut" + (sortKey == SortKey.Size ? arrow : "");
        HdrDate.Content = "Değiştirilme" + (sortKey == SortKey.Date ? arrow : "");
    }

    // =====================================================================
    // Disk haritası (ağaç + treemap)
    // =====================================================================
    void PopulateTree()
    {
        var rootRow = new TreeRow(idx, idx.Root, 0, idx.Alloc[idx.Root]);
        treeRows.ReplaceAll(new[] { rootRow });
        Expand(rootRow);
        suppressMapSync = true;
        TreeList.SelectedIndex = 0;
        suppressMapSync = false;
    }

    void Expand(TreeRow row)
    {
        if (row == null || !row.HasChildren || row.IsExpanded) return;
        int pos = treeRows.IndexOf(row);
        if (pos < 0) return;
        long pa = idx.Alloc[row.Index];
        var rows = idx.ChildrenBySize(row.Index).Select(k => new TreeRow(idx, k, row.Depth + 1, pa)).ToList();
        row.IsExpanded = true;
        var sel = TreeList.SelectedItem;
        treeRows.InsertRange(pos + 1, rows);
        if (sel != null) { suppressMapSync = true; TreeList.SelectedItem = sel; suppressMapSync = false; }
    }

    void Collapse(TreeRow row)
    {
        if (row == null || !row.IsExpanded) return;
        int pos = treeRows.IndexOf(row);
        int end = pos + 1;
        while (end < treeRows.Count && treeRows[end].Depth > row.Depth) end++;
        row.IsExpanded = false;
        var sel = TreeList.SelectedItem;
        treeRows.RemoveRange(pos + 1, end - pos - 1);
        suppressMapSync = true;
        TreeList.SelectedItem = sel != null && treeRows.Contains((TreeRow)sel) ? sel : row;
        suppressMapSync = false;
    }

    void Toggle(TreeRow row)
    {
        if (row.IsExpanded) Collapse(row); else Expand(row);
    }

    void Chevron_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TreeRow row)
        {
            e.Handled = true;
            Toggle(row);
        }
    }

    void TreeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressMapSync || idx == null) return;
        if (TreeList.SelectedItem is not TreeRow row) return;
        int folder = row.IsDir ? row.Index : idx.Parent[row.Index];
        if (folder < 0) folder = idx.Root;
        if (Map.RootItem != folder) Map.SetRoot(idx, folder);
        Map.SetSelected(row.IsDir ? -1 : row.Index);
        UpdateMapHeader();
    }

    void UpdateMapHeader()
    {
        if (idx == null) return;
        int r = Map.RootItem;
        if (r < 0) return;
        MapRootName.Text = r == idx.Root ? idx.RootPath : idx.Name(r);
        MapRootSub.Text = $"{Fmt.Size(idx.Alloc[r])} · {Fmt.Num(idx.FileCount[r])} dosya · {idx.GetPath(r)}";
    }

    void Map_Hovered(int i)
    {
        if (i < 0 || idx == null) { MapHover.Text = " "; return; }
        long viewAlloc = Math.Max(1, idx.Alloc[Map.RootItem]);
        MapHover.Text = $"{idx.GetPath(i)}   ·   {Fmt.Size(idx.Alloc[i])}   ·   görünümün {Fmt.Percent((double)idx.Alloc[i] / viewAlloc)}'i";
    }

    void Map_Clicked(int i)
    {
        Map.SetSelected(i);
        RevealInTree(i, false);
    }

    void Map_Opened(int i)
    {
        if (idx == null || !idx.IsDir(i)) return;
        Map.SetRoot(idx, i);
        UpdateMapHeader();
        RevealInTree(i, false);
    }

    void MapUp_Click(object sender, RoutedEventArgs e)
    {
        if (idx == null) return;
        int r = Map.RootItem;
        if (r >= 0 && r != idx.Root && idx.Parent[r] >= 0)
        {
            Map.SetRoot(idx, idx.Parent[r]);
            UpdateMapHeader();
            RevealInTree(Map.RootItem, false);
        }
    }

    void MapHome_Click(object sender, RoutedEventArgs e)
    {
        if (idx == null) return;
        Map.SetRoot(idx, idx.Root);
        UpdateMapHeader();
    }

    /// <summary>Ağaçta öğeye kadar olan klasörleri açıp öğeyi seçer.</summary>
    void RevealInTree(int item, bool syncMap)
    {
        if (idx == null || item < 0) return;
        var chain = new List<int>();
        for (int c = item; c >= 0; c = idx.Parent[c])
        {
            chain.Add(c);
            if (c == idx.Root) break;
        }
        chain.Reverse();
        suppressMapSync = !syncMap;
        try
        {
            int from = 0;
            for (int k = 0; k < chain.Count - 1; k++)
            {
                var row = FindRow(chain[k], ref from);
                if (row == null) return;
                Expand(row);
            }
            int f2 = 0;
            var target = FindRow(item, ref f2);
            if (target != null)
            {
                TreeList.SelectedItem = target;
                TreeList.ScrollIntoView(target);
            }
        }
        finally { suppressMapSync = false; }
    }

    TreeRow FindRow(int index, ref int from)
    {
        for (int i = from; i < treeRows.Count; i++)
            if (treeRows[i].Index == index) { from = i; return treeRows[i]; }
        for (int i = 0; i < Math.Min(from, treeRows.Count); i++)
            if (treeRows[i].Index == index) { from = i; return treeRows[i]; }
        return null;
    }

    void ShowInMap(int i)
    {
        if (idx == null || i < 0) return;
        NavMap.IsChecked = true;
        int folder = idx.IsDir(i) ? i : idx.Parent[i];
        Map.SetRoot(idx, folder < 0 ? idx.Root : folder);
        Map.SetSelected(idx.IsDir(i) ? -1 : i);
        UpdateMapHeader();
        Dispatcher.BeginInvoke(() => RevealInTree(i, false), DispatcherPriority.Loaded);
    }

    // =====================================================================
    // Dosya türleri
    // =====================================================================
    void PopulateTypes()
    {
        long total = Math.Max(1, stats.TotalAlloc);
        CategoryCards.ItemsSource = Enumerable.Range(0, 10).OrderByDescending(c => stats.CatSize[c]).Select(c => new SimpleBar
        {
            Title = Categories.Names[c],
            Value = Fmt.Size(stats.CatSize[c]),
            Sub = $"{Fmt.Num(stats.CatCount[c])} dosya · {Fmt.Percent((double)stats.CatSize[c] / total)}",
            Color = Brushes2.Cat((Category)c),
            Glyph = Categories.Glyphs[c],
            Key = "cat:" + c
        }).ToList();

        long max = stats.Exts.Count > 0 ? Math.Max(1, stats.Exts[0].Size) : 1;
        TypesList.ItemsSource = stats.Exts.Where(x => x.Size > 0 || x.Count > 0).Select(x => new SimpleBar
        {
            Title = x.Ext.Length > 0 ? "." + x.Ext : "(uzantısız)",
            Tag = Categories.Names[(int)x.Cat],
            Sub = Fmt.Num(x.Count),
            Value = Fmt.Size(x.Size),
            Fraction = (double)x.Size / max,
            Color = Brushes2.Cat(x.Cat),
            Glyph = Categories.Glyphs[(int)x.Cat],
            Key = x.Ext.Length > 0 ? "ext:" + x.Ext : null
        }).ToList();
    }

    void Types_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TypesList.SelectedItem is SimpleBar b) ActivateKey(b.Key);
    }

    // =====================================================================
    // Ortak liste işlemleri (bağlam menüsü, klavye, çift tık)
    // =====================================================================
    List<int> SelectedIndices(ListBox list)
    {
        if (list == SearchList) return SearchList.SelectedItems.OfType<FileRow>().Select(r => r.Index).ToList();
        if (list == TreeList) return TreeList.SelectedItems.OfType<TreeRow>().Select(r => r.Index).ToList();
        return new List<int>();
    }

    ListBox MenuTarget(object sender) =>
        (sender as MenuItem)?.Parent is ContextMenu cm ? cm.PlacementTarget as ListBox : null;

    void Ctx_Open(object sender, RoutedEventArgs e)
    {
        var l = MenuTarget(sender);
        if (l != null) foreach (var i in SelectedIndices(l).Take(5)) OpenPath(idx.GetPath(i));
    }

    void Ctx_Reveal(object sender, RoutedEventArgs e)
    {
        var l = MenuTarget(sender);
        var sel = l != null ? SelectedIndices(l) : null;
        if (sel?.Count > 0) RevealPath(idx.GetPath(sel[0]));
    }

    void Ctx_ShowInMap(object sender, RoutedEventArgs e)
    {
        var l = MenuTarget(sender);
        var sel = l != null ? SelectedIndices(l) : null;
        if (sel?.Count > 0) ShowInMap(sel[0]);
    }

    void Ctx_CopyPath(object sender, RoutedEventArgs e)
    {
        var l = MenuTarget(sender);
        if (l == null) return;
        var paths = SelectedIndices(l).Select(i => idx.GetPath(i)).ToList();
        if (paths.Count > 0) try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); } catch { }
    }

    async void Ctx_Recycle(object sender, RoutedEventArgs e)
    {
        var l = MenuTarget(sender);
        if (l != null) await DeleteIndices(SelectedIndices(l), false);
    }

    async void Ctx_Delete(object sender, RoutedEventArgs e)
    {
        var l = MenuTarget(sender);
        if (l != null) await DeleteIndices(SelectedIndices(l), true);
    }

    async void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox l || idx == null) return;
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            bool perm = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            await DeleteIndices(SelectedIndices(l), perm);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (l == TreeList && TreeList.SelectedItem is TreeRow r && r.IsDir) Toggle(r);
            else foreach (var i in SelectedIndices(l).Take(5)) OpenPath(idx.GetPath(i));
        }
        else if (l == TreeList && TreeList.SelectedItem is TreeRow row)
        {
            if (e.Key == Key.Right) { Expand(row); e.Handled = true; }
            else if (e.Key == Key.Left)
            {
                e.Handled = true;
                if (row.IsExpanded) Collapse(row);
                else
                {
                    int p = idx.Parent[row.Index];
                    int f = 0;
                    var pr = p >= 0 ? FindRow(p, ref f) : null;
                    if (pr != null) { TreeList.SelectedItem = pr; TreeList.ScrollIntoView(pr); }
                }
            }
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var paths = SelectedIndices(l).Select(i => idx.GetPath(i)).ToList();
            if (paths.Count > 0) try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); } catch { }
        }
    }

    void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox l || idx == null) return;
        // Ok (chevron) üstündeki çift tıkları yok say
        for (var d = e.OriginalSource as DependencyObject; d != null && d is not ListBoxItem; d = VisualTreeHelper.GetParent(d))
            if (d is Border b && b.Tag is TreeRow) return;
        if (ItemsControl.ContainerFromElement(l, e.OriginalSource as DependencyObject) is not ListBoxItem) return;
        if (l == TreeList)
        {
            if (TreeList.SelectedItem is TreeRow r)
            {
                if (r.IsDir) Toggle(r); else OpenPath(idx.GetPath(r.Index));
            }
        }
        else if (l.SelectedItem is FileRow fr) OpenPath(idx.GetPath(fr.Index));
    }

    static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Dialog.Info(Application.Current.MainWindow, "Açılamadı", ex.Message); }
    }

    static void RevealPath(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    // =====================================================================
    // Silme
    // =====================================================================
    static Task RunSta(Action a)
    {
        var tcs = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            try { a(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    /// <summary>Yolları siler; silinenleri döndürür.</summary>
    async Task<HashSet<string>> DeletePaths(List<string> paths, bool permanent)
    {
        if (permanent) await Task.Run(() => Cleanup.DeletePermanent(paths));
        else
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            await RunSta(() => Cleanup.Recycle(paths, hwnd));
        }
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths) if (!File.Exists(p) && !Directory.Exists(p)) gone.Add(p);
        return gone;
    }

    async Task DeleteIndices(List<int> items, bool permanent)
    {
        if (idx == null || items.Count == 0) return;
        var set = new HashSet<int>(items);
        // Üst klasörü de seçilmiş olanları çıkar
        items = items.Where(i =>
        {
            for (int p = idx.Parent[i]; p >= 0; p = idx.Parent[p])
            {
                if (set.Contains(p)) return false;
                if (p == idx.Root) break;
            }
            return true;
        }).ToList();

        var blocked = new List<string>();
        var ok = new List<int>();
        foreach (var i in items)
        {
            var why = Cleanup.WhyBlocked(idx.GetPath(i));
            if (why != null) blocked.Add(idx.Name(i) + ": " + why); else ok.Add(i);
        }
        if (blocked.Count > 0)
            Dialog.Info(this, "Bazı öğeler korunuyor", string.Join("\n", blocked.Take(6)));
        if (ok.Count == 0) return;

        long size = ok.Sum(i => idx.Alloc[i]);
        var names = string.Join("\n", ok.Take(5).Select(i => "•  " + idx.Name(i))) + (ok.Count > 5 ? $"\n…ve {ok.Count - 5} öğe daha" : "");
        string msg = permanent
            ? $"{ok.Count} öğe ({Fmt.Size(size)}) KALICI olarak silinecek. Bu işlem geri alınamaz.\n\n{names}"
            : $"{ok.Count} öğe ({Fmt.Size(size)}) geri dönüşüm kutusuna gönderilecek.\nYer, kutu boşaltıldığında açılır.\n\n{names}";
        if (!Dialog.Confirm(this, permanent ? "Kalıcı olarak sil" : "Geri dönüşüm kutusuna gönder", msg,
                permanent ? "Kalıcı sil" : "Gönder", danger: true)) return;

        var paths = ok.Select(i => idx.GetPath(i)).ToList();
        StatusText.Text = "Siliniyor…";
        var gone = await DeletePaths(paths, permanent);
        int done = 0;
        for (int k = 0; k < ok.Count; k++)
            if (gone.Contains(paths[k])) { idx.MarkDeleted(ok[k]); done++; }
        StatusText.Text = $"{done}/{ok.Count} öğe silindi" + (done < ok.Count ? " (bazıları kullanımda / erişilemedi)" : "");
        AfterIndexChanged();
    }

    void AfterIndexChanged()
    {
        if (idx == null) return;
        // Silinen satırları ağaçtan çıkar, oranları yenile
        var keep = treeRows.Where(r => !idx.IsDeleted(r.Index)).ToList();
        var sel = TreeList.SelectedItem as TreeRow;
        suppressMapSync = true;
        treeRows.ReplaceAll(keep);
        foreach (var r in keep)
        {
            int p = idx.Parent[r.Index];
            r.Recalc(r.Index == idx.Root || p < 0 ? idx.Alloc[r.Index] : idx.Alloc[p]);
        }
        if (sel != null && keep.Contains(sel)) TreeList.SelectedItem = sel;
        suppressMapSync = false;
        if (Map.RootItem >= 0 && idx.IsDeleted(Map.RootItem))
        {
            int r = Map.RootItem;
            while (r >= 0 && idx.IsDeleted(r)) r = idx.Parent[r];
            Map.SetRoot(idx, r < 0 ? idx.Root : r);
        }
        else Map.Refresh();
        UpdateMapHeader();
        UpdateTitleInfo();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) RunSearch();
        _ = RecomputeStats();
        _ = ComputeCleanCounts();
        LoadDrives();
        RefreshRecycle();
    }

    async Task RecomputeStats()
    {
        var index = idx;
        var st = await Task.Run(() => DiskStats.Compute(index));
        if (index != idx) return;
        stats = st;
        PopulateOverview();
        PopulateTypes();
    }

    // =====================================================================
    // Temizlik
    // =====================================================================
    void RefreshRecycle()
    {
        var (s, c) = Cleanup.QueryRecycleBin();
        RbSize.Text = Fmt.Size(s);
        RbSub.Text = c == 0 ? "Kutu boş" : $"{Fmt.Num(c)} öğe · boşaltınca bu kadar yer açılır";
    }

    void RecycleRefresh_Click(object sender, RoutedEventArgs e) => RefreshRecycle();

    async void RecycleEmpty_Click(object sender, RoutedEventArgs e)
    {
        var (s, c) = Cleanup.QueryRecycleBin();
        if (c == 0) { Dialog.Info(this, "Geri Dönüşüm Kutusu", "Kutu zaten boş."); return; }
        if (!Dialog.Confirm(this, "Geri dönüşüm kutusunu boşalt",
                $"Tüm sürücülerdeki {Fmt.Num(c)} öğe ({Fmt.Size(s)}) kalıcı olarak silinecek. Bu işlem geri alınamaz.",
                "Boşalt", danger: true)) return;
        await RunSta(() => Cleanup.EmptyRecycleBin(IntPtr.Zero));
        var (s2, _) = Cleanup.QueryRecycleBin();
        RefreshRecycle();
        // İndeksten de düş
        if (idx != null)
        {
            int rb = idx.Find(Path.Combine(idx.RootPath, "$Recycle.Bin"));
            if (rb >= 0)
                foreach (var sid in idx.Children(rb).ToList())
                    foreach (var item in idx.Children(sid).ToList())
                        if (!idx.NameSpan(item).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                        {
                            var p = idx.GetPath(item);
                            if (!File.Exists(p) && !Directory.Exists(p)) idx.MarkDeleted(item);
                        }
            AfterIndexChanged();
        }
        Dialog.Info(this, "Tamamlandı", $"{Fmt.Size(Math.Max(0, s - s2))} yer açıldı.");
    }

    async Task MeasureDownloadsCard()
    {
        var dl = Native.GetDownloadsFolder();
        var (size, count) = await Task.Run(() => Cleanup.MeasureFolder(dl, CancellationToken.None));
        DlSize.Text = Fmt.Size(size);
        DlSub.Text = $"{Fmt.Num(count)} dosya · {dl}";
    }

    async Task MeasureTempCard()
    {
        TempSize.Text = "…";
        var locs = await Task.Run(() =>
        {
            var l = Cleanup.TempLocations();
            foreach (var t in l) (t.Size, t.Count) = Cleanup.MeasureFolder(t.Path, CancellationToken.None);
            return l;
        });
        tempLocs = locs;
        TempSize.Text = Fmt.Size(locs.Sum(t => t.Size));
        TempSub.Text = $"{locs.Count} konumda {Fmt.Num(locs.Sum(t => t.Count))} dosya";
    }

    async Task ComputeCleanCounts()
    {
        if (idx == null) return;
        var index = idx;
        bool appData = IncludeAppData.IsChecked == true;
        var (ef, ed, bigCount, bigSize) = await Task.Run(() =>
        {
            var a = Cleanup.FindEmptyFiles(index, appData, CancellationToken.None).Count;
            var b = Cleanup.FindEmptyFolders(index, appData, CancellationToken.None).Count;
            var big = BigFiles(index);
            return (a, b, big.Count, big.Sum(i => index.Alloc[i]));
        });
        if (index != idx) return;
        EmptyFilesCount.Text = Fmt.Num(ef);
        EmptyDirsCount.Text = Fmt.Num(ed);
        BigCount.Text = Fmt.Num(bigCount);
        BigSub.Text = $"toplam {Fmt.Size(bigSize)} · sistem dosyaları hariç";
    }

    static List<int> BigFiles(FileIndex index)
    {
        var res = new List<int>();
        const long limit = 1L << 30;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        int winIdx = index.Find(win);
        for (int i = 0; i < index.Count; i++)
        {
            if (index.IsDir(i) || index.IsDeleted(i) || index.Alloc[i] < limit) continue;
            if (winIdx >= 0 && index.IsAncestor(winIdx, i)) continue;
            if (Cleanup.WhyBlocked(index.GetPath(i)) != null) continue;
            res.Add(i);
        }
        res.Sort((a, b) => index.Alloc[b].CompareTo(index.Alloc[a]));
        return res;
    }

    void IncludeAppData_Changed(object sender, RoutedEventArgs e) => _ = ComputeCleanCounts();

    CleanItem ItemFromIndex(int i, CleanKind kind)
    {
        var c = Categories.Of(idx, i);
        bool dir = idx.IsDir(i);
        return new CleanItem
        {
            Index = i,
            Path = idx.GetPath(i),
            Name = idx.Name(i),
            Folder = idx.GetParentPath(i),
            Size = idx.Alloc[i] > 0 ? idx.Alloc[i] : idx.Size[i],
            Mtime = idx.Mtime[i],
            Kind = kind,
            Glyph = dir ? "" : Categories.Glyphs[(int)c],
            IconBrush = dir ? Brushes2.Folder : Brushes2.Cat(c)
        };
    }

    async Task ShowDetail(DetailMode mode, string title, string sub, Func<CancellationToken, List<CleanItem>> load)
    {
        detailCts?.Cancel();
        var cts = detailCts = new CancellationTokenSource();
        detailMode = mode;
        DetailTitle.Text = title;
        DetailSub.Text = sub;
        AgeFilter.Visibility = mode == DetailMode.Downloads ? Visibility.Visible : Visibility.Collapsed;
        detailItems.ReplaceAll(Array.Empty<CleanItem>());
        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailProgress.Visibility = Visibility.Visible;
        UpdateDetailSelection();
        List<CleanItem> list;
        try { list = await Task.Run(() => load(cts.Token), cts.Token); }
        catch (OperationCanceledException) { return; }
        finally { if (cts == detailCts) DetailProgress.Visibility = Visibility.Collapsed; }
        if (cts != detailCts) return;
        detailItems.ReplaceAll(list);
        DetailEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetailSelection();
    }

    async void TempReview_Click(object sender, RoutedEventArgs e)
    {
        await ShowDetail(DetailMode.Temp, "Geçici dosyalar & önbellekler",
            "Seçili konumların İÇERİĞİ kalıcı olarak silinir (geçici dosyalar kutuya gönderilmez). Kullanımdaki dosyalar atlanır, Windows bunları yeniden oluşturur.",
            ct =>
            {
                var locs = Cleanup.TempLocations();
                foreach (var t in locs) (t.Size, t.Count) = Cleanup.MeasureFolder(t.Path, ct);
                return locs.Where(t => t.Count > 0).OrderByDescending(t => t.Size).Select(t => new CleanItem
                {
                    Name = t.Title,
                    Folder = $"{t.Path}   ({Fmt.Num(t.Count)} dosya)",
                    Path = t.Path,
                    Size = t.Size,
                    Kind = CleanKind.FolderContents,
                    Glyph = "",
                    IconBrush = Brushes2.Folder,
                    IsChecked = true
                }).ToList();
            });
    }

    async void DownloadsReview_Click(object sender, RoutedEventArgs e)
    {
        var dl = Native.GetDownloadsFolder();
        var index = idx;
        AgeFilter.SelectedIndex = -1;
        await ShowDetail(DetailMode.Downloads, "İndirilenler", dl + "  —  eskileri seçmek için sağdaki filtreyi kullan", ct =>
        {
            var list = new List<CleanItem>();
            if (!Directory.Exists(dl)) return list;
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            foreach (var info in new DirectoryInfo(dl).EnumerateFileSystemInfos("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (info.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                bool dir = info is DirectoryInfo;
                long size;
                if (dir)
                {
                    int i = index?.Find(info.FullName) ?? -1;
                    size = i >= 0 ? index.Alloc[i] : Cleanup.MeasureFolder(info.FullName, ct).size;
                }
                else size = ((FileInfo)info).Length;
                var cat = dir ? Category.Other : Categories.OfExt(info.Extension.TrimStart('.'));
                list.Add(new CleanItem
                {
                    Name = info.Name,
                    Folder = dir ? "Klasör" : info.Extension.TrimStart('.').ToUpperInvariant() + " dosyası",
                    Path = info.FullName,
                    Size = size,
                    Mtime = info.LastWriteTimeUtc.ToFileTimeUtc(),
                    Kind = dir ? CleanKind.Folder : CleanKind.File,
                    Glyph = dir ? "" : Categories.Glyphs[(int)cat],
                    IconBrush = dir ? Brushes2.Folder : Brushes2.Cat(cat)
                });
            }
            return list.OrderByDescending(x => x.Size).ToList();
        });
    }

    async void EmptyFiles_Click(object sender, RoutedEventArgs e)
    {
        if (idx == null) { Dialog.Info(this, "Önce tara", "Boş dosyaları bulmak için önce sürücüyü tara."); return; }
        var index = idx;
        bool appData = IncludeAppData.IsChecked == true;
        await ShowDetail(DetailMode.EmptyFiles, "Boş dosyalar (0 bayt)",
            "Windows, Program Files, ProgramData, AppData, .git ve node_modules hariç. Yer kazandırmaz ama dağınıklığı azaltır.",
            ct => Cleanup.FindEmptyFiles(index, appData, ct).Select(i => ItemFromIndex(i, CleanKind.File)).ToList());
    }

    async void EmptyDirs_Click(object sender, RoutedEventArgs e)
    {
        if (idx == null) { Dialog.Info(this, "Önce tara", "Boş klasörleri bulmak için önce sürücüyü tara."); return; }
        var index = idx;
        bool appData = IncludeAppData.IsChecked == true;
        await ShowDetail(DetailMode.EmptyDirs, "Boş klasörler",
            "İçinde (alt klasörleri dahil) hiç dosya olmayan klasörler. Sistem ve program klasörleri hariç.",
            ct => Cleanup.FindEmptyFolders(index, appData, ct).Select(i => ItemFromIndex(i, CleanKind.Folder)).ToList());
    }

    async void BigFiles_Click(object sender, RoutedEventArgs e)
    {
        if (idx == null) { Dialog.Info(this, "Önce tara", "Büyük dosyaları bulmak için önce sürücüyü tara."); return; }
        var index = idx;
        await ShowDetail(DetailMode.BigFiles, "Büyük dosyalar (1 GB ve üstü)",
            "Windows klasörü ve sistem dosyaları (pagefile, hiberfil…) hariç. Neyi sildiğinden emin ol — oyun / program dosyası olabilir.",
            ct => BigFiles(index).Select(i => ItemFromIndex(i, CleanKind.File)).ToList());
    }

    void UpdateDetailSelection()
    {
        int n = 0; long size = 0;
        foreach (var x in detailItems) if (x.IsChecked) { n++; size += x.Size; }
        DetailDeleteBtn.IsEnabled = n > 0;
        DetailDeleteText.Text = n > 0 ? $"Seçilenleri sil  ({Fmt.Num(n)} · {Fmt.Size(size)})" : "Seçilenleri sil";
    }

    void SetAllChecked(Func<CleanItem, bool> pred)
    {
        suppressCheck = true;
        foreach (var x in detailItems) x.IsChecked = pred(x);
        suppressCheck = false;
        UpdateDetailSelection();
    }

    void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllChecked(_ => true);
    void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllChecked(_ => false);

    void AgeFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AgeFilter.SelectedItem is not ComboBoxItem it || !int.TryParse(it.Tag as string, out int days)) return;
        var limit = DateTime.UtcNow.AddDays(-days).ToFileTimeUtc();
        SetAllChecked(x => days == 0 || (x.Mtime > 0 && x.Mtime < limit));
    }

    void DetailList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            var sel = DetailList.SelectedItems.OfType<CleanItem>().ToList();
            if (sel.Count == 0) return;
            bool v = !sel[0].IsChecked;
            suppressCheck = true;
            foreach (var x in sel) x.IsChecked = v;
            suppressCheck = false;
            UpdateDetailSelection();
        }
    }

    void DetailList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DetailList.SelectedItem is CleanItem c) RevealPath(c.Path);
    }

    async void DetailDelete_Click(object sender, RoutedEventArgs e)
    {
        var sel = detailItems.Where(x => x.IsChecked).ToList();
        if (sel.Count == 0) return;
        long size = sel.Sum(x => x.Size);

        if (detailMode == DetailMode.Temp)
        {
            if (!Dialog.Confirm(this, "Geçici dosyaları temizle",
                    $"{sel.Count} konumun içeriği ({Fmt.Size(size)}) kalıcı olarak silinecek. Kullanımdaki dosyalar atlanır.",
                    "Temizle", danger: true)) return;
            DetailProgress.Visibility = Visibility.Visible;
            long freed = await Task.Run(() => sel.Sum(x => Cleanup.CleanFolderContents(x.Path, CancellationToken.None)));
            DetailProgress.Visibility = Visibility.Collapsed;
            Dialog.Info(this, "Tamamlandı", $"{Fmt.Size(freed)} yer açıldı." + (freed < size ? $"\n{Fmt.Size(size - freed)} kullanımda olduğu için atlandı." : ""));
            MarkMissingUnder(sel.Select(x => x.Path));
            _ = MeasureTempCard();
            TempReview_Click(null, null);
            return;
        }

        bool permanent = ModePermanent.IsChecked == true;
        var blocked = sel.Where(x => Cleanup.WhyBlocked(x.Path) != null).ToList();
        sel = sel.Except(blocked).ToList();
        if (blocked.Count > 0) Dialog.Info(this, "Bazı öğeler korunuyor", string.Join("\n", blocked.Take(6).Select(b => b.Name + ": " + Cleanup.WhyBlocked(b.Path))));
        if (sel.Count == 0) return;
        size = sel.Sum(x => x.Size);
        string msg = permanent
            ? $"{Fmt.Num(sel.Count)} öğe ({Fmt.Size(size)}) KALICI olarak silinecek. Bu işlem geri alınamaz."
            : $"{Fmt.Num(sel.Count)} öğe ({Fmt.Size(size)}) geri dönüşüm kutusuna gönderilecek.\nYerin açılması için sonra kutuyu boşaltman gerekir.";
        if (!Dialog.Confirm(this, permanent ? "Kalıcı olarak sil" : "Geri dönüşüm kutusuna gönder", msg,
                permanent ? "Kalıcı sil" : "Gönder", danger: true)) return;

        DetailProgress.Visibility = Visibility.Visible;
        var gone = await DeletePaths(sel.Select(x => x.Path).ToList(), permanent);
        DetailProgress.Visibility = Visibility.Collapsed;

        var removed = sel.Where(x => gone.Contains(x.Path)).ToList();
        if (idx != null)
            foreach (var x in removed)
            {
                int i = x.Index >= 0 && !idx.IsDeleted(x.Index) ? x.Index : idx.Find(x.Path);
                if (i >= 0) idx.MarkDeleted(i);
            }
        detailItems.ReplaceAll(detailItems.Where(x => !removed.Contains(x)).ToList());
        DetailEmpty.Visibility = detailItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetailSelection();
        long freedSize = removed.Sum(x => x.Size);
        Dialog.Info(this, "Tamamlandı",
            $"{Fmt.Num(removed.Count)} öğe {(permanent ? "silindi" : "geri dönüşüm kutusuna gönderildi")} ({Fmt.Size(freedSize)})." +
            (removed.Count < sel.Count ? $"\n{sel.Count - removed.Count} öğe kullanımda olduğu ya da erişilemediği için silinemedi." : "") +
            (permanent ? "" : "\n\nYer açmak için Geri Dönüşüm Kutusu kartından “Boşalt”a bas."));
        if (detailMode == DetailMode.Downloads) _ = MeasureDownloadsCard();
        AfterIndexChanged();
    }

    /// <summary>Klasör içeriği temizlendikten sonra artık var olmayan öğeleri indeksten düşer.</summary>
    void MarkMissingUnder(IEnumerable<string> folders)
    {
        if (idx == null) return;
        foreach (var f in folders)
        {
            int d = idx.Find(f);
            if (d < 0) continue;
            foreach (var c in idx.Children(d).ToList())
            {
                var p = idx.GetPath(c);
                if (!File.Exists(p) && !Directory.Exists(p)) idx.MarkDeleted(c);
            }
        }
        AfterIndexChanged();
    }
}

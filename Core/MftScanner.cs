using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace JinjaDisk.Core;

/// <summary>
/// NTFS Master File Table'ı doğrudan diskten okur (WizTree / Everything yöntemi).
/// Klasörleri tek tek gezmek yerine tüm dosya kayıtlarını ardışık büyük bloklarla okuyup
/// paralel ayrıştırır. Yönetici izni gerektirir.
/// </summary>
public static unsafe class MftScanner
{
    const uint SigFILE = 0x454C4946; // "FILE"
    const ulong RefMask = 0x0000FFFFFFFFFFFFUL;
    const int RootRecord = 5;
    const int ChunkBytes = 8 * 1024 * 1024;

    struct ExtInfo
    {
        public int Base;
        public string Name;
        public byte Ns;
        public int ParentRec;
        public bool HasData;
        public long Size, Alloc;
    }

    sealed class State
    {
        public int N, RecSize;
        public int[] ParentRec;
        public string[] Names;
        public byte[] NameNs;     // 0 = yok, 1 = DOS, 2 = Win32/POSIX
        public long[] Size, Alloc, Mtime;
        public uint[] Attr;
        public byte[] Kind;       // 0 = kullanılmıyor, 1 = dosya, 2 = klasör
        public ConcurrentQueue<ExtInfo> Ext = new();
    }

    public static FileIndex Scan(char drive, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        string vol = @"\\.\" + char.ToUpperInvariant(drive) + ":";
        using var h = Native.CreateFileW(vol, Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Birim açılamadı");

        Native.NTFS_VOLUME_DATA_BUFFER vd;
        if (!Native.DeviceIoControl(h, Native.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, &vd,
                sizeof(Native.NTFS_VOLUME_DATA_BUFFER), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "NTFS birim bilgisi alınamadı");

        int recSize = (int)vd.BytesPerFileRecordSegment;
        int cluster = (int)vd.BytesPerCluster;
        int sector = (int)Math.Max(512, vd.BytesPerSector);
        if (recSize < 256 || recSize > 65536) throw new InvalidOperationException("Geçersiz MFT kayıt boyutu");

        // ---- Kayıt 0 ($MFT) -> MFT'nin diskteki parçaları (data runs) ----
        int firstRead = RoundUp(Math.Max(recSize, cluster), sector);
        byte* tmp = (byte*)NativeMemory.AlignedAlloc((nuint)firstRead, 4096);
        List<(long lcn, long clusters)> runs;
        try
        {
            ReadAt(h, vd.MftStartLcn * cluster, tmp, firstRead);
            if (*(uint*)tmp != SigFILE || !Fixup(tmp, recSize)) throw new InvalidOperationException("MFT kaydı 0 okunamadı");
            runs = ParseMftRuns(tmp, recSize);
        }
        finally { NativeMemory.AlignedFree(tmp); }
        if (runs.Count == 0) throw new InvalidOperationException("MFT parçaları bulunamadı");

        long totalBytes = 0;
        foreach (var r in runs) totalBytes += r.clusters * cluster;
        totalBytes = Math.Min(totalBytes, vd.MftValidDataLength);
        int n = (int)(totalBytes / recSize);

        var st = new State
        {
            N = n, RecSize = recSize,
            ParentRec = new int[n], Names = new string[n], NameNs = new byte[n],
            Size = new long[n], Alloc = new long[n], Mtime = new long[n], Attr = new uint[n], Kind = new byte[n]
        };

        // ---- Okuma planı: runs -> en fazla ChunkBytes'lık parçalar ----
        var chunks = new List<(long diskOff, long mftOff, int bytes)>();
        long mftPos = 0;
        foreach (var (lcn, clusters) in runs)
        {
            long runBytes = clusters * cluster;
            long done = 0;
            while (done < runBytes && mftPos + done < totalBytes)
            {
                long take = Math.Min(ChunkBytes, Math.Min(runBytes - done, totalBytes - (mftPos + done)));
                take = RoundUp((int)take, sector);
                chunks.Add((lcn * cluster + done, mftPos + done, (int)take));
                done += take;
            }
            mftPos += runBytes;
            if (mftPos >= totalBytes) break;
        }

        // ---- Çift tamponlu boru hattı: bir blok okunurken önceki paralel ayrıştırılır ----
        nint bufA = (nint)NativeMemory.AlignedAlloc(ChunkBytes, 4096);
        nint bufB = (nint)NativeMemory.AlignedAlloc(ChunkBytes, 4096);
        try
        {
            var pending = Task.Run(() => ReadAt(h, chunks[0].diskOff, (byte*)bufA, chunks[0].bytes));
            for (int k = 0; k < chunks.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                pending.Wait(ct);
                nint cur = (k & 1) == 0 ? bufA : bufB;
                nint next = (k & 1) == 0 ? bufB : bufA;
                if (k + 1 < chunks.Count)
                {
                    var c = chunks[k + 1];
                    pending = Task.Run(() => ReadAt(h, c.diskOff, (byte*)next, c.bytes));
                }

                var ch = chunks[k];
                int firstRec = (int)(ch.mftOff / recSize);
                int recs = Math.Min(ch.bytes / recSize, n - firstRec);
                if (recs > 0)
                {
                    const int part = 2048;
                    int parts = (recs + part - 1) / part;
                    Parallel.For(0, parts, new ParallelOptions { CancellationToken = ct }, pi =>
                    {
                        int s = pi * part, e = Math.Min(recs, s + part);
                        byte* basePtr = (byte*)cur;
                        for (int r = s; r < e; r++) ParseRecord(st, basePtr + (long)r * recSize, firstRec + r);
                    });
                }
                progress?.Report(new ScanProgress("MFT okunuyor", 0.9 * (k + 1) / chunks.Count, firstRec + recs));
            }
        }
        finally
        {
            NativeMemory.AlignedFree((void*)bufA);
            NativeMemory.AlignedFree((void*)bufB);
        }

        // ---- Uzantı kayıtlarını (attribute list) ana kayıtlara uygula ----
        while (st.Ext.TryDequeue(out var x))
        {
            int b = x.Base;
            if (b < 0 || b >= n || st.Kind[b] == 0) continue;
            if (x.Name != null)
            {
                byte rank = x.Ns == 2 ? (byte)1 : (byte)2;
                if (st.NameNs[b] < rank)
                {
                    st.Names[b] = x.Name; st.NameNs[b] = rank; st.ParentRec[b] = x.ParentRec;
                }
            }
            if (x.HasData) { st.Size[b] = x.Size; st.Alloc[b] = x.Alloc; }
        }

        progress?.Report(new ScanProgress("Ağaç kuruluyor", 0.93, n));
        return BuildIndex(st, char.ToUpperInvariant(drive) + @":\", cluster);
    }

    static FileIndex BuildIndex(State st, string rootPath, int cluster)
    {
        int n = st.N;
        // Kökten erişilebilirlik: 0 = bilinmiyor, 1 = evet, 2 = hayır
        var reach = new byte[n];
        if (n > RootRecord && st.Kind[RootRecord] == 2) reach[RootRecord] = 1;
        var path = new List<int>(64);
        for (int r = 0; r < n; r++)
        {
            if (reach[r] != 0) continue;
            if (st.Kind[r] == 0 || st.Names[r] == null) { reach[r] = 2; continue; }
            path.Clear();
            int c = r;
            byte result = 2;
            while (true)
            {
                if (c < 0 || c >= n) { result = 2; break; }
                if (reach[c] != 0) { result = reach[c]; break; }
                if (st.Kind[c] == 0 || st.Names[c] == null || path.Count > 2048) { result = 2; break; }
                path.Add(c);
                reach[c] = 3; // ziyarette (döngü koruması)
                int p = st.ParentRec[c];
                if (p == c) { result = 2; break; }
                c = p;
                if (c < n && reach[c] == 3) { result = 2; break; }
            }
            foreach (var x in path) reach[x] = result;
        }

        var map = new int[n];
        int count = 0;
        for (int r = 0; r < n; r++) map[r] = reach[r] == 1 ? count++ : -1;

        var b = new FileIndexBuilder(count + 16);
        for (int r = 0; r < n; r++)
        {
            if (map[r] < 0) continue;
            bool dir = st.Kind[r] == 2;
            int p = r == RootRecord ? -1 : map[st.ParentRec[r]];
            var name = r == RootRecord ? rootPath.TrimEnd('\\').AsSpan() : st.Names[r].AsSpan();
            b.Add(p, name, st.Size[r], st.Alloc[r], st.Mtime[r], st.Attr[r], dir);
        }
        var idx = b.Build(rootPath, map[RootRecord]);
        idx.UsedMft = true;
        idx.ClusterSize = cluster;
        return idx;
    }

    static void ParseRecord(State st, byte* p, int recNo)
    {
        if (*(uint*)p != SigFILE) return;
        int recSize = st.RecSize;
        if (!Fixup(p, recSize)) return;
        ushort flags = *(ushort*)(p + 0x16);
        if ((flags & 1) == 0) return;
        ulong baseRef = *(ulong*)(p + 0x20) & RefMask;
        bool isExt = baseRef != 0;

        string name = null; byte nameRank = 0; int parentRec = 0;
        bool hasData = false; long size = 0, alloc = 0, mtime = 0; uint attr = 0;

        int off = *(ushort*)(p + 0x14);
        int used = Math.Min(*(int*)(p + 0x18), recSize);
        while (off + 16 <= used)
        {
            uint type = *(uint*)(p + off);
            if (type == 0xFFFFFFFF) break;
            int len = *(int*)(p + off + 4);
            if (len < 16 || off + len > used) break;
            byte nonRes = p[off + 8];
            byte attrNameLen = p[off + 9];
            byte* a = p + off;

            switch (type)
            {
                case 0x10: // $STANDARD_INFORMATION
                    if (nonRes == 0)
                    {
                        byte* v = a + *(ushort*)(a + 0x14);
                        mtime = *(long*)(v + 0x08);
                        attr = *(uint*)(v + 0x20);
                    }
                    break;
                case 0x30: // $FILE_NAME
                    if (nonRes == 0)
                    {
                        byte* v = a + *(ushort*)(a + 0x14);
                        byte ns = v[0x41];
                        byte rank = ns == 2 ? (byte)1 : (byte)2; // DOS (8.3) adı en düşük öncelik
                        if (rank > nameRank)
                        {
                            byte nl = v[0x40];
                            name = new string((char*)(v + 0x42), 0, nl);
                            nameRank = rank;
                            parentRec = (int)(*(ulong*)v & RefMask);
                        }
                    }
                    break;
                case 0x80: // $DATA (yalnızca adsız ana akış)
                    if (attrNameLen == 0)
                    {
                        if (nonRes == 0)
                        {
                            size = *(uint*)(a + 0x10);
                            alloc = 0; // MFT kaydının içinde duruyor
                            hasData = true;
                        }
                        else if (*(long*)(a + 0x10) == 0) // StartVCN == 0
                        {
                            ushort aflags = *(ushort*)(a + 0x0C);
                            alloc = *(long*)(a + 0x28);
                            size = *(long*)(a + 0x30);
                            if ((aflags & 0x8001) != 0 && len >= 0x48) alloc = *(long*)(a + 0x40); // sıkıştırılmış / seyrek
                            hasData = true;
                        }
                    }
                    break;
            }
            off += len;
        }

        if (isExt)
        {
            if (name != null || hasData)
                st.Ext.Enqueue(new ExtInfo
                {
                    Base = (int)Math.Min(baseRef, int.MaxValue), Name = name, Ns = nameRank == 1 ? (byte)2 : (byte)1,
                    ParentRec = parentRec, HasData = hasData, Size = size, Alloc = alloc
                });
            return;
        }

        st.Kind[recNo] = (flags & 2) != 0 ? (byte)2 : (byte)1;
        st.Mtime[recNo] = mtime;
        st.Attr[recNo] = attr;
        if (name != null)
        {
            // Uzantı kaydı önce işlendiyse daha iyi adı ezme
            if (st.NameNs[recNo] < nameRank)
            {
                st.Names[recNo] = name; st.NameNs[recNo] = nameRank; st.ParentRec[recNo] = parentRec;
            }
        }
        if (hasData) { st.Size[recNo] = size; st.Alloc[recNo] = alloc; }
    }

    static bool Fixup(byte* p, int recSize)
    {
        int usaOfs = *(ushort*)(p + 4);
        int usaCount = *(ushort*)(p + 6);
        if (usaCount == 0 || usaOfs + usaCount * 2 > recSize) return false;
        ushort* usa = (ushort*)(p + usaOfs);
        ushort usn = usa[0];
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * 512 - 2;
            if (pos + 2 > recSize) break;
            ushort* end = (ushort*)(p + pos);
            if (*end != usn) return false;
            *end = usa[i];
        }
        return true;
    }

    static List<(long, long)> ParseMftRuns(byte* p, int recSize)
    {
        var runs = new List<(long, long)>();
        int off = *(ushort*)(p + 0x14);
        int used = Math.Min(*(int*)(p + 0x18), recSize);
        while (off + 16 <= used)
        {
            uint type = *(uint*)(p + off);
            if (type == 0xFFFFFFFF) break;
            int len = *(int*)(p + off + 4);
            if (len < 16 || off + len > used) break;
            byte* a = p + off;
            if (type == 0x80 && a[8] != 0 && a[9] == 0)
            {
                byte* rp = a + *(ushort*)(a + 0x20);
                byte* end = a + len;
                long lcn = 0;
                while (rp < end && *rp != 0)
                {
                    int lenSize = *rp & 0x0F, offSize = (*rp >> 4) & 0x0F;
                    rp++;
                    if (lenSize == 0 || lenSize > 8 || offSize > 8 || rp + lenSize + offSize > end) break;
                    long runLen = 0;
                    for (int i = 0; i < lenSize; i++) runLen |= (long)rp[i] << (8 * i);
                    rp += lenSize;
                    if (offSize == 0) { rp += 0; continue; } // seyrek parça (MFT'de olmaz)
                    long delta = 0;
                    for (int i = 0; i < offSize; i++) delta |= (long)rp[i] << (8 * i);
                    if ((rp[offSize - 1] & 0x80) != 0) delta |= -1L << (8 * offSize); // işaret genişletme
                    rp += offSize;
                    lcn += delta;
                    runs.Add((lcn, runLen));
                }
                break;
            }
            off += len;
        }
        return runs;
    }

    static void ReadAt(SafeFileHandle h, long offset, byte* buf, int bytes)
    {
        var ov = new NativeOverlapped
        {
            OffsetLow = (int)(offset & 0xFFFFFFFF),
            OffsetHigh = (int)(offset >> 32)
        };
        int done = 0;
        while (done < bytes)
        {
            long o = offset + done;
            ov.OffsetLow = (int)(o & 0xFFFFFFFF);
            ov.OffsetHigh = (int)(o >> 32);
            if (!Native.ReadFile(h, buf + done, bytes - done, out int read, &ov))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "MFT okunamadı");
            if (read == 0) break;
            done += read;
        }
    }

    static int RoundUp(int v, int m) => (v + m - 1) / m * m;
}

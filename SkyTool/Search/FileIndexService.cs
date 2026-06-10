using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using SkyTool.Common;

namespace SkyTool.Search;

public class SearchResult
{
    public string Name { get; set; }
    public string FullPath { get; set; }
    public bool IsDir { get; set; }
}

/// <summary>
/// 全盘文件名索引。
/// 管理员模式：枚举 NTFS MFT（FSCTL_ENUM_USN_DATA）秒级建索引，并用 USN 日志实时增量更新 —— Everything 同原理。
/// 非管理员：降级为后台目录遍历建索引。
/// </summary>
public class FileIndexService
{
    public static readonly FileIndexService Instance = new();

    private class Entry
    {
        public string Name;
        public ulong ParentFrn;
        public bool IsDir;
    }

    private class VolumeIndex
    {
        public char Drive;
        public ulong RootFrn;
        public ulong JournalId;
        public long NextUsn;
        public readonly ConcurrentDictionary<ulong, Entry> Entries = new();
    }

    private const ulong FrnMask = 0x0000FFFFFFFFFFFF; // 去掉序列号，只留 48 位索引

    /// <summary>降级模式条目：Dir 是共享引用（同目录的所有子项指向同一个字符串），省内存。</summary>
    private class WalkEntry
    {
        public string Name;
        public string Dir;
        public bool IsDir;
    }

    private readonly List<VolumeIndex> _volumes = new();
    private List<WalkEntry> _walkIndex = new();             // 降级模式索引
    private CancellationTokenSource _cts = new();

    public bool IsAdminMode { get; private set; }
    public bool Ready { get; private set; }
    public string Status { get; private set; } = "索引尚未开始";
    public event Action StatusChanged;

    public long TotalCount
    {
        get
        {
            long n = _walkIndex.Count;
            foreach (var v in _volumes) n += v.Entries.Count;
            return n;
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => Build(_cts.Token));
    }

    public void Stop() => _cts.Cancel();

    private void SetStatus(string s)
    {
        Status = s;
        StatusChanged?.Invoke();
    }

    private void Build(CancellationToken ct)
    {
        IsAdminMode = IsAdministrator();
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .ToList();

        if (IsAdminMode)
        {
            var ntfs = drives.Where(d => string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var d in ntfs)
            {
                if (ct.IsCancellationRequested) return;
                char letter = char.ToUpperInvariant(d.Name[0]);
                SetStatus($"正在索引 {letter}: 盘（MFT 极速模式）…");
                try
                {
                    var vi = EnumVolume(letter, ct);
                    lock (_volumes) _volumes.Add(vi);
                }
                catch (Exception ex)
                {
                    SetStatus($"{letter}: 索引失败：{ex.Message}");
                }
            }
            Ready = true;
            SetStatus($"已索引 {TotalCount:N0} 个文件 · 极速模式 · 实时更新中");
            // 每个卷一个监听线程，跟踪 USN 日志增量
            foreach (var vi in _volumes)
            {
                var v = vi;
                var t = new Thread(() => MonitorVolume(v, ct)) { IsBackground = true, Name = $"usn-{v.Drive}" };
                t.Start();
            }
        }
        else
        {
            SetStatus("正在后台扫描磁盘建立索引（以管理员运行可启用秒级 MFT 索引）…");
            WalkDrives(drives, ct);
            Ready = true;
            SetStatus($"已索引 {TotalCount:N0} 个文件 · 普通模式（以管理员重启可启用极速索引与实时更新）");
        }
    }

    // ---------- MFT 枚举 ----------

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenVolume(char drive)
    {
        var h = Native.CreateFile($@"\\.\{drive}:", Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"无法打开卷 {drive}:（错误码 {Marshal.GetLastWin32Error()}）");
        return h;
    }

    private static ulong GetRootFrn(char drive)
    {
        using var h = Native.CreateFile($@"{drive}:\", Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"无法打开 {drive}:\\");
        if (!Native.GetFileInformationByHandle(h, out var info))
            throw new IOException("GetFileInformationByHandle 失败");
        return (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) & FrnMask;
    }

    private VolumeIndex EnumVolume(char drive, CancellationToken ct)
    {
        using var volume = OpenVolume(drive);
        var journal = QueryOrCreateJournal(volume);

        var vi = new VolumeIndex
        {
            Drive = drive,
            RootFrn = GetRootFrn(drive),
            JournalId = journal.UsnJournalID,
            NextUsn = journal.NextUsn,
        };

        const int bufSize = 1024 * 1024;
        IntPtr med = Marshal.AllocHGlobal(Marshal.SizeOf<Native.MFT_ENUM_DATA_V0>());
        IntPtr buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            var enumData = new Native.MFT_ENUM_DATA_V0
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = journal.NextUsn,
            };

            while (!ct.IsCancellationRequested)
            {
                Marshal.StructureToPtr(enumData, med, false);
                if (!Native.DeviceIoControl(volume, Native.FSCTL_ENUM_USN_DATA,
                        med, Marshal.SizeOf<Native.MFT_ENUM_DATA_V0>(),
                        buf, bufSize, out int bytes, IntPtr.Zero))
                {
                    break; // ERROR_HANDLE_EOF = 枚举完成
                }

                enumData.StartFileReferenceNumber = (ulong)Marshal.ReadInt64(buf);
                int offset = 8;
                while (offset < bytes)
                {
                    int recLen = ParseUsnRecord(buf, offset, out ulong frn, out ulong parent,
                        out bool isDir, out string name, out _);
                    if (recLen <= 0) break;
                    vi.Entries[frn] = new Entry { Name = name, ParentFrn = parent, IsDir = isDir };
                    offset += recLen;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(med);
            Marshal.FreeHGlobal(buf);
        }
        return vi;
    }

    private static Native.USN_JOURNAL_DATA_V0 QueryOrCreateJournal(Microsoft.Win32.SafeHandles.SafeFileHandle volume)
    {
        IntPtr outBuf = Marshal.AllocHGlobal(Marshal.SizeOf<Native.USN_JOURNAL_DATA_V0>());
        try
        {
            if (!Native.DeviceIoControl(volume, Native.FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero, 0, outBuf, Marshal.SizeOf<Native.USN_JOURNAL_DATA_V0>(),
                    out _, IntPtr.Zero))
            {
                // 卷上没有 USN 日志，创建一个
                var create = new Native.CREATE_USN_JOURNAL_DATA { MaximumSize = 64 * 1024 * 1024, AllocationDelta = 8 * 1024 * 1024 };
                IntPtr inBuf = Marshal.AllocHGlobal(Marshal.SizeOf<Native.CREATE_USN_JOURNAL_DATA>());
                try
                {
                    Marshal.StructureToPtr(create, inBuf, false);
                    if (!Native.DeviceIoControl(volume, Native.FSCTL_CREATE_USN_JOURNAL,
                            inBuf, Marshal.SizeOf<Native.CREATE_USN_JOURNAL_DATA>(),
                            IntPtr.Zero, 0, out _, IntPtr.Zero))
                        throw new IOException($"无法创建 USN 日志（错误码 {Marshal.GetLastWin32Error()}）");
                }
                finally { Marshal.FreeHGlobal(inBuf); }

                if (!Native.DeviceIoControl(volume, Native.FSCTL_QUERY_USN_JOURNAL,
                        IntPtr.Zero, 0, outBuf, Marshal.SizeOf<Native.USN_JOURNAL_DATA_V0>(),
                        out _, IntPtr.Zero))
                    throw new IOException($"查询 USN 日志失败（错误码 {Marshal.GetLastWin32Error()}）");
            }
            return Marshal.PtrToStructure<Native.USN_JOURNAL_DATA_V0>(outBuf);
        }
        finally { Marshal.FreeHGlobal(outBuf); }
    }

    /// <summary>解析 USN_RECORD_V2，返回记录长度；失败返回 0。</summary>
    private static int ParseUsnRecord(IntPtr buf, int offset, out ulong frn, out ulong parentFrn,
        out bool isDir, out string name, out uint reason)
    {
        frn = 0; parentFrn = 0; isDir = false; name = null; reason = 0;
        int recLen = Marshal.ReadInt32(buf, offset);
        if (recLen < 60) return 0;

        frn = (ulong)Marshal.ReadInt64(buf, offset + 8) & FrnMask;
        parentFrn = (ulong)Marshal.ReadInt64(buf, offset + 16) & FrnMask;
        reason = (uint)Marshal.ReadInt32(buf, offset + 40);
        uint attrs = (uint)Marshal.ReadInt32(buf, offset + 52);
        isDir = (attrs & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
        ushort nameLen = (ushort)Marshal.ReadInt16(buf, offset + 56);
        ushort nameOff = (ushort)Marshal.ReadInt16(buf, offset + 58);
        name = Marshal.PtrToStringUni(buf + offset + nameOff, nameLen / 2);
        return recLen;
    }

    // ---------- USN 实时监听 ----------

    private void MonitorVolume(VolumeIndex vi, CancellationToken ct)
    {
        const int bufSize = 256 * 1024;
        IntPtr inBuf = Marshal.AllocHGlobal(Marshal.SizeOf<Native.READ_USN_JOURNAL_DATA_V0>());
        IntPtr buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            using var volume = OpenVolume(vi.Drive);
            while (!ct.IsCancellationRequested)
            {
                var read = new Native.READ_USN_JOURNAL_DATA_V0
                {
                    StartUsn = vi.NextUsn,
                    ReasonMask = Native.USN_REASON_FILE_CREATE | Native.USN_REASON_FILE_DELETE |
                                 Native.USN_REASON_RENAME_OLD_NAME | Native.USN_REASON_RENAME_NEW_NAME,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = vi.JournalId,
                };
                Marshal.StructureToPtr(read, inBuf, false);

                if (!Native.DeviceIoControl(volume, Native.FSCTL_READ_USN_JOURNAL,
                        inBuf, Marshal.SizeOf<Native.READ_USN_JOURNAL_DATA_V0>(),
                        buf, bufSize, out int bytes, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err is Native.ERROR_JOURNAL_ENTRY_DELETED or Native.ERROR_JOURNAL_DELETE_IN_PROGRESS or Native.ERROR_JOURNAL_NOT_ACTIVE)
                    {
                        // 日志被截断/重建：重新全量枚举该卷
                        try
                        {
                            var rebuilt = EnumVolume(vi.Drive, ct);
                            vi.Entries.Clear();
                            foreach (var kv in rebuilt.Entries) vi.Entries[kv.Key] = kv.Value;
                            vi.JournalId = rebuilt.JournalId;
                            vi.NextUsn = rebuilt.NextUsn;
                            continue;
                        }
                        catch { /* 卷可能暂时不可用，稍后重试 */ }
                    }
                    Thread.Sleep(3000);
                    continue;
                }

                if (bytes > 8)
                {
                    vi.NextUsn = Marshal.ReadInt64(buf);
                    int offset = 8;
                    while (offset < bytes)
                    {
                        int recLen = ParseUsnRecord(buf, offset, out ulong frn, out ulong parent,
                            out bool isDir, out string name, out uint reason);
                        if (recLen <= 0) break;

                        if ((reason & (Native.USN_REASON_FILE_DELETE | Native.USN_REASON_RENAME_OLD_NAME)) != 0)
                            vi.Entries.TryRemove(frn, out _);
                        else if ((reason & (Native.USN_REASON_FILE_CREATE | Native.USN_REASON_RENAME_NEW_NAME)) != 0)
                            vi.Entries[frn] = new Entry { Name = name, ParentFrn = parent, IsDir = isDir };

                        offset += recLen;
                    }
                }
                else
                {
                    vi.NextUsn = Marshal.ReadInt64(buf); // 没有新记录时也推进游标
                    Thread.Sleep(1500);
                }
            }
        }
        catch { /* 监听线程退出（卷被卸载等），保留已有索引 */ }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(buf);
        }
    }

    // ---------- 降级模式：目录遍历 ----------

    private void WalkDrives(List<DriveInfo> drives, CancellationToken ct)
    {
        var perDrive = new List<WalkEntry>[drives.Count];
        long count = 0;
        Parallel.For(0, drives.Count, new ParallelOptions { CancellationToken = ct }, di =>
        {
            var list = new List<WalkEntry>(1 << 18);
            var stack = new Stack<string>();
            stack.Push(drives[di].Name.TrimEnd('\\'));
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) return;
                string dir = stack.Pop();
                IEnumerable<FileSystemInfo> children;
                try
                {
                    children = new DirectoryInfo(dir + '\\').EnumerateFileSystemInfos();
                }
                catch { continue; } // 无权限/路径过长，跳过

                foreach (var info in children)
                {
                    bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    bool reparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                    // Dir 共享同一个 dir 字符串实例，不为每个文件单独存完整路径
                    list.Add(new WalkEntry { Name = info.Name, Dir = dir, IsDir = isDir });
                    if (isDir && !reparse)
                        stack.Push(dir + '\\' + info.Name);

                    long n = Interlocked.Increment(ref count);
                    if (n % 100000 == 0)
                        SetStatus($"正在扫描… 已收录 {n:N0} 个文件");
                }
            }
            perDrive[di] = list;
        });

        var merged = new List<WalkEntry>((int)Math.Min(count, int.MaxValue));
        foreach (var l in perDrive)
            if (l != null) merged.AddRange(l);
        _walkIndex = merged;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
    }

    // ---------- 查询 ----------

    public List<SearchResult> Query(string text, int limit, CancellationToken ct)
    {
        var terms = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return new List<SearchResult>();

        var matches = new ConcurrentBag<SearchResult>();
        int cap = Math.Max(limit * 5, 2000); // 多收一些再排序
        int collected = 0;

        bool Match(string name)
        {
            foreach (var t in terms)
                if (!name.Contains(t, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        try
        {
            List<VolumeIndex> vols;
            lock (_volumes) vols = _volumes.ToList();

            foreach (var vi in vols)
            {
                Parallel.ForEach(vi.Entries, new ParallelOptions { CancellationToken = ct }, kv =>
                {
                    if (collected >= cap) return;
                    if (Match(kv.Value.Name))
                    {
                        var path = BuildPath(vi, kv.Key);
                        if (path != null)
                        {
                            matches.Add(new SearchResult { Name = kv.Value.Name, FullPath = path, IsDir = kv.Value.IsDir });
                            Interlocked.Increment(ref collected);
                        }
                    }
                });
                if (collected >= cap) break;
            }

            var walk = _walkIndex;
            if (walk.Count > 0 && collected < cap)
            {
                Parallel.ForEach(walk, new ParallelOptions { CancellationToken = ct }, r =>
                {
                    if (collected >= cap) return;
                    if (Match(r.Name))
                    {
                        matches.Add(new SearchResult { Name = r.Name, FullPath = r.Dir + '\\' + r.Name, IsDir = r.IsDir });
                        Interlocked.Increment(ref collected);
                    }
                });
            }
        }
        catch (OperationCanceledException) { return new List<SearchResult>(); }

        string first = terms[0];
        return matches
            .OrderByDescending(r => r.Name.StartsWith(first, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => r.Name.Length)
            .ThenBy(r => r.FullPath.Length)
            .Take(limit)
            .ToList();
    }

    /// <summary>由 FRN 逐级向上拼出完整路径。</summary>
    private static string BuildPath(VolumeIndex vi, ulong frn)
    {
        var parts = new List<string>(16);
        ulong cur = frn;
        for (int depth = 0; depth < 64; depth++)
        {
            if (cur == vi.RootFrn) break;
            if (!vi.Entries.TryGetValue(cur, out var e)) break; // 父级缺失，挂到盘根
            parts.Add(e.Name);
            cur = e.ParentFrn;
        }
        if (parts.Count == 0) return null;
        parts.Reverse();
        return $@"{vi.Drive}:\{string.Join('\\', parts)}";
    }
}

namespace SkyTool.Common;

public static class MemoryUtil
{
    /// <summary>
    /// 整理托管堆并把暂时用不到的物理内存还给操作系统。
    /// 适合常驻托盘的小工具在「干完一波活」（如建完索引、关闭搜索窗）后调用，
    /// 让任务管理器里的占用回落到实际需要的水平；需要时系统会自动把页换回来。
    /// </summary>
    public static void Trim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Native.EmptyWorkingSet(Native.GetCurrentProcess());
        }
        catch { /* 回收失败不影响功能 */ }
    }
}

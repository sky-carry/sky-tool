using System.Windows;
using System.Windows.Interop;

namespace SkyTool.Common;

/// <summary>全局热键注册与分发。挂在主窗口的 HwndSource 上。</summary>
public class HotkeyManager : IDisposable
{
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly Dictionary<int, string> _labels = new();
    private HwndSource _source;
    private IntPtr _hwnd;
    private int _nextId = 0xA000;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);
    }

    /// <summary>注册热键，返回实际生效的热键描述（失败返回 null）。</summary>
    public string Register(uint modifiers, uint vk, string label, Action action)
    {
        int id = _nextId++;
        if (!Native.RegisterHotKey(_hwnd, id, modifiers, vk))
            return null;
        _handlers[id] = action;
        _labels[id] = label;
        return label;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys)
            Native.UnregisterHotKey(_hwnd, id);
        _handlers.Clear();
        _source?.RemoveHook(WndProc);
    }
}

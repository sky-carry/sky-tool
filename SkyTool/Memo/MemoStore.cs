using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyTool.Memo;

public class TodoItem : INotifyPropertyChanged
{
    private bool _done;
    private string _text;
    private DateTime? _remindAt;
    private bool _notified;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedDate { get; set; } = DateTime.Today;

    public string Text
    {
        get => _text;
        set { _text = value; OnChanged(); }
    }

    public bool Done
    {
        get => _done;
        set { _done = value; OnChanged(); OnChanged(nameof(StatusIcon)); }
    }

    public DateTime? RemindAt
    {
        get => _remindAt;
        set { _remindAt = value; OnChanged(); OnChanged(nameof(RemindText)); OnChanged(nameof(HasReminder)); }
    }

    public bool Notified
    {
        get => _notified;
        set { _notified = value; OnChanged(); }
    }

    [JsonIgnore] public string RemindText => RemindAt.HasValue ? $"⏰ {RemindAt:HH:mm}" : "";
    [JsonIgnore] public bool HasReminder => RemindAt.HasValue;
    [JsonIgnore] public string StatusIcon => Done ? "✓" : "";

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class MemoSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public bool Collapsed { get; set; }
    public bool Visible { get; set; } = true;
}

/// <summary>待办的加载/保存（%AppData%\SkyTool）。</summary>
public class MemoStore
{
    public static readonly MemoStore Instance = new();

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyTool");
    private static readonly string TasksFile = Path.Combine(Dir, "tasks.json");
    private static readonly string SettingsFile = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ObservableCollection<TodoItem> Items { get; } = new();
    public MemoSettings Settings { get; private set; } = new();

    private readonly object _saveLock = new();

    public void Load()
    {
        try
        {
            if (File.Exists(TasksFile))
            {
                var items = JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(TasksFile)) ?? new();
                Items.Clear();
                foreach (var it in items) Items.Add(it);
            }
            if (File.Exists(SettingsFile))
                Settings = JsonSerializer.Deserialize<MemoSettings>(File.ReadAllText(SettingsFile)) ?? new();
        }
        catch { /* 数据损坏时从空白开始 */ }

        foreach (var it in Items)
            it.PropertyChanged += (_, _) => Save();
        Items.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (TodoItem it in e.NewItems)
                    it.PropertyChanged += (_, _) => Save();
            Save();
        };
    }

    public void Save()
    {
        try
        {
            lock (_saveLock)
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(TasksFile, JsonSerializer.Serialize(Items.ToList(), JsonOpts));
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Settings, JsonOpts));
            }
        }
        catch { /* 磁盘暂时不可写，忽略 */ }
    }
}

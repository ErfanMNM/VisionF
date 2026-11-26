using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

/// <summary>
/// An alternative view of the existing config editor. Field instances, validation,
/// save confirmation and hot-apply are shared with the original configuration tab.
/// The diagram is a navigation aid, not a second device configuration store.
/// </summary>
internal sealed partial class SettingsV2ViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public SettingsV2ViewModel(ConfigEditorViewModel editor, MainViewModel main)
    {
        Editor = editor;
        _main = main;
        Scanner = Create("Scanner", "Scanner", "TCP / COM", "Máy quét cầm tay");
        Gateway = Create("MultiConnect", "MultiConnect", "GATEWAY", "Chuyển tiếp giao thức");
        Camera = Create("Camera", "Camera", "TCP", "Đầu đọc mã");
        Printer = Create("Print", "Printer", "TCP", "Máy in G10");
        QrBox = Create("Plc", "QRBox", "FINS / UDP", "PLC Omron · kết quả và bộ đếm");
        Checkee = Create("Checkee", "Checkee", "HTTPS", "Gửi mã kích hoạt");
        Mes = Create("PoServer", "Nhận PO từ MES", "HTTP API", "Máy chủ tiếp nhận lệnh");
        Production = Create("Po", "Production Order", "HTTP API", "Máy chủ quản lý PO");
        Web = Create("Web", "Web sản xuất", "WEB", "Trang Checkee nhúng");
        InputNodes = [Scanner, Gateway, Camera, Printer];
        OutputNodes = [Checkee, Mes, Production, Web];
        AllNodes = [.. InputNodes, QrBox, .. OutputNodes];
        var flowKeys = AllNodes.Select(node => node.Section.Key).ToHashSet(StringComparer.Ordinal);
        OtherSections = editor.Sections.Where(section => !flowKeys.Contains(section.Key)).ToArray();
        _selectedSection = QrBox.Section;
        _selectedTitle = QrBox.Title;
        _selectedProtocol = QrBox.Protocol;
        QrBox.IsSelected = true;
        _main.PropertyChanged += OnRuntimeChanged;
        _main.Print.PropertyChanged += OnRuntimeChanged;
    }

    public ConfigEditorViewModel Editor { get; }
    public SettingsV2NodeViewModel Scanner { get; }
    public SettingsV2NodeViewModel Gateway { get; }
    public SettingsV2NodeViewModel Camera { get; }
    public SettingsV2NodeViewModel Printer { get; }
    public SettingsV2NodeViewModel QrBox { get; }
    public SettingsV2NodeViewModel Checkee { get; }
    public SettingsV2NodeViewModel Mes { get; }
    public SettingsV2NodeViewModel Production { get; }
    public SettingsV2NodeViewModel Web { get; }
    public IReadOnlyList<SettingsV2NodeViewModel> InputNodes { get; }
    public IReadOnlyList<SettingsV2NodeViewModel> OutputNodes { get; }
    public IReadOnlyList<SettingsV2NodeViewModel> AllNodes { get; }
    public IReadOnlyList<ConfigSectionViewModel> OtherSections { get; }

    [ObservableProperty]
    private ConfigSectionViewModel _selectedSection;

    [ObservableProperty]
    private string _selectedTitle;

    [ObservableProperty]
    private string _selectedProtocol;

    [ObservableProperty]
    private bool _isOtherSettingsVisible;

    [ObservableProperty]
    private ConfigSectionViewModel? _selectedOtherSection;

    public string RuntimeSummary => $"Camera: {_main.CameraStatusText} · PLC: {_main.PlcStatusText}";

    private SettingsV2NodeViewModel Create(string key, string title, string protocol, string caption) =>
        new(Editor.Sections.Single(section => section.Key == key), title, protocol, caption,
            SelectNode, key switch
            {
                "Camera" => () => _main.CameraStatusText,
                "Plc" => () => _main.PlcStatusText,
                "Print" => () => _main.Print.ConnectionText,
                _ => null,
            });

    private void SelectNode(SettingsV2NodeViewModel selected)
    {
        foreach (var node in AllNodes) node.IsSelected = ReferenceEquals(node, selected);
        SelectedSection = selected.Section;
        SelectedTitle = selected.Title;
        SelectedProtocol = selected.Protocol;
        IsOtherSettingsVisible = false;
    }

    partial void OnSelectedOtherSectionChanged(ConfigSectionViewModel? value)
    {
        if (value is null) return;
        foreach (var node in AllNodes) node.IsSelected = false;
        SelectedSection = value;
        SelectedTitle = value.Title;
        SelectedProtocol = "CÀI ĐẶT KHÁC";
    }

    [RelayCommand]
    private void ShowFlow() => SelectNode(QrBox);

    [RelayCommand]
    private void ShowOtherSettings()
    {
        SelectedOtherSection ??= OtherSections.FirstOrDefault();
        OnSelectedOtherSectionChanged(SelectedOtherSection);
        IsOtherSettingsVisible = true;
    }

    private void OnRuntimeChanged(object? sender, PropertyChangedEventArgs args)
    {
        foreach (var node in AllNodes) node.RefreshRuntime();
        OnPropertyChanged(nameof(RuntimeSummary));
    }
}

internal sealed partial class SettingsV2NodeViewModel : ViewModelBase
{
    private static readonly IBrush Online = Brush.Parse("#0f9f63");
    private static readonly IBrush Warning = Brush.Parse("#b7791f");
    private static readonly IBrush Offline = Brush.Parse("#d92d3a");
    private static readonly IBrush Configured = Brush.Parse("#64748b");
    private readonly Func<string>? _runtimeStatus;

    public SettingsV2NodeViewModel(ConfigSectionViewModel section, string title,
        string protocol, string caption, Action<SettingsV2NodeViewModel> select,
        Func<string>? runtimeStatus)
    {
        Section = section;
        Title = title;
        Protocol = protocol;
        Caption = caption;
        _runtimeStatus = runtimeStatus;
        SelectCommand = new RelayCommand(() => select(this));
        foreach (var field in section.Fields) field.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Endpoint));
            RefreshRuntime();
        };
    }

    public ConfigSectionViewModel Section { get; }
    public string Title { get; }
    public string Protocol { get; }
    public string Caption { get; }
    public IRelayCommand SelectCommand { get; }

    [ObservableProperty]
    private bool _isSelected;

    // Addresses describe the draft being edited. Live status always comes from
    // the same device ViewModels as the original app; no demo connectivity flags.
    public string Endpoint => Section.Key switch
    {
        "Scanner" => Value("TcpHost") + ":" + Value("TcpPort"),
        "Camera" or "Plc" or "Print" => Value("Ip") + ":" + Value("Port"),
        "Checkee" or "Po" => Value("BaseUrl"),
        "PoServer" => Value("BindAddress") + ":" + Value("Port"),
        "Web" => Value("Url"),
        _ => "Serial / TCP / UDP → TCP",
    };

    public string StatusText => _runtimeStatus?.Invoke() ??
        (Section.Fields.Any(configField => configField.JsonPath.EndsWith(":Enabled", StringComparison.Ordinal))
            ? (Value("Enabled").Equals("true", StringComparison.OrdinalIgnoreCase)
                ? "Bật trong cấu hình" : "Tắt trong cấu hình")
            : "Cấu hình dịch vụ");

    public IBrush StatusBrush => _runtimeStatus is null ? Configured : StatusText switch
    {
        "Đã kết nối" => Online,
        "Đang kết nối lại" => Warning,
        _ => Offline,
    };

    public void RefreshRuntime()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    private string Value(string name) => Section.Fields
        .FirstOrDefault(field => field.JsonPath == Section.Key + ":" + name)?.TextValue ?? "—";
}

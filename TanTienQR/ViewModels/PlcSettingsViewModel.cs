using System.Collections.ObjectModel;
using Avalonia.Controls;
using ClassLibraryMT.Auth;
using ClassLibraryMT.Logging;
using ClassLibraryMT.Plc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using TanTienQR.Services;
using TanTienQR.Views;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho tab "Chỉnh PLC".
///
/// Ba phần chính:
/// - Webview xem camera (IP lấy từ cấu hình <c>Camera.Ip</c>).
/// - Bảng tham số PLC tùy chỉnh (Name / Ô nhớ / kiểu / Read-Write) — đọc ghi PLC thật.
/// - Khung Recipe: lưu bộ giá trị cho từng tham số và áp dụng xuống PLC.
/// </summary>
internal sealed partial class PlcSettingsViewModel : ViewModelBase
{
    private readonly IPlcSettingsService _plc;
    private readonly IUserActionLogger _actionLog;
    private readonly CameraOptions _cameraOptions;
    private readonly IUserService _users;
    private readonly ISessionContext _session;
    /// <summary>Cửa sổ chính — dùng để mở hộp thoại.</summary>
    private Window? _window;

    /// <summary>Thông báo trạng thái (lỗi / thành công).</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>True khi thông báo là lỗi (hiển thị màu đỏ).</summary>
    [ObservableProperty]
    public partial bool IsStatusError { get; set; }

    /// <summary>True khi đang tải dữ liệu.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// True khi webview camera đã được phép khởi tạo.
    ///
    /// <c>NativeWebView</c> tạo WebView2 native — việc này khá nặng. Chỉ bật
    /// khi người dùng thực sự mở tab Chỉnh PLC, tránh làm chậm khởi động app.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCameraWebEnabled { get; set; }

    /// <summary>
    /// True khi bảng tham số / recipe đang hiển thị.
    ///
    /// Khi False thì cột trái ẩn lại và camera chiếm trọn màn hình — tiện
    /// cho người vận hành theo dõi camera mà không bị bảng che khuất.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ParameterPanelWidth))]
    public partial bool IsParameterPanelVisible { get; set; } = true;

    /// <summary>
    /// Chiều rộng cột trái (bảng ô nhớ + recipe).
    ///
    /// Khi bảng bị ẩn thì chiều rộng = 0 để camera chiếm trọn màn hình.
    /// </summary>
    public double ParameterPanelWidth => IsParameterPanelVisible ? 430 : 0;

    /// <summary>Danh sách tham số hiển thị trên bảng.</summary>
    public ObservableCollection<PlcParamRowViewModel> ParameterRows { get; } = [];

    /// <summary>
    /// Các dòng tham số đang soạn (nhấn "+" để thêm nhanh, chưa lưu vào DB).
    /// </summary>
    public ObservableCollection<NewParamRowViewModel> DraftRows { get; } = [];

    /// <summary>True khi đang có dòng soạn dở dang.</summary>
    public bool HasDraftRows => DraftRows.Count > 0;

    /// <summary>
    /// True khi bảng hoàn toàn trống (không có dòng đã lưu lẫn dòng đang soạn).
    /// </summary>
    public bool IsTableEmpty => ParameterRows.Count == 0 && DraftRows.Count == 0;

    /// <summary>True khi đã có ít nhất một tham số trong bảng.</summary>
    public bool HasParameters => ParameterRows.Count > 0;

    /// <summary>Số lượng tham số hiện có (dùng cho badge trên tiêu đề).</summary>
    public int ParameterCount => ParameterRows.Count;

    /// <summary>Số lượng recipe hiện có (dùng cho badge trên tiêu đề).</summary>
    public int RecipeCount => Recipes.Count;

    /// <summary>Danh sách recipe.</summary>
    public ObservableCollection<PlcRecipe> Recipes { get; } = [];

    /// <summary>Recipe đang chọn.</summary>
    [ObservableProperty]
    public partial PlcRecipe? SelectedRecipe { get; set; }

    /// <summary>Tên recipe mới (ô nhập tạo recipe).</summary>
    [ObservableProperty]
    public partial string NewRecipeName { get; set; } = string.Empty;

    /// <summary>
    /// URL webview camera (dạng <see cref="Uri"/>).
    ///
    /// <c>NativeWebView.Source</c> yêu cầu kiểu <see cref="Uri"/>, nên tao
    /// sẵn ở đây. Nếu IP camera không hợp lệ thì trả về <c>about:blank</c>.
    /// </summary>
    public Uri CameraMonitorUri => BuildCameraUri();

    /// <summary>URL webview camera dạng chuỗi (để hiển thị trên thanh địa chỉ).</summary>
    public string CameraMonitorUrl => CameraMonitorUri.ToString();

    /// <summary>True khi có cấu hình IP camera để hiển thị webview.</summary>
    public bool HasCameraUrl => BuildCameraUri().AbsoluteUri != "about:blank";

    /// <summary>
    /// Khởi tạo view model.
    /// </summary>
    /// <param name="plc">Service quản lý tham số PLC.</param>
    /// <param name="actionLog">Logger thao tác người dùng.</param>
    /// <param name="cameraOptions">Cấu hình đầu đọc mã (lấy IP camera).</param>
    /// <param name="users">Service quản lý tài khoản (xác thực mật khẩu khi xoá recipe).</param>
    /// <param name="session">Ngữ cảnh phiên đăng nhập.</param>
    public PlcSettingsViewModel(
        IPlcSettingsService plc,
        IUserActionLogger actionLog,
        IOptions<CameraOptions> cameraOptions,
        IUserService users,
        ISessionContext session)
    {
        _plc = plc;
        _actionLog = actionLog;
        _cameraOptions = cameraOptions.Value;
        _users = users;
        _session = session;

        // Cập nhật cờ "đã có tham số" + số lượng mỗi khi danh sách thay đổi.
        ParameterRows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasParameters));
            OnPropertyChanged(nameof(ParameterCount));
            OnPropertyChanged(nameof(IsTableEmpty));
        };

        // Cập nhật cờ "đang soạn" mỗi khi có thêm/bớt dòng draft.
        DraftRows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDraftRows));
            OnPropertyChanged(nameof(IsTableEmpty));
        };
    }

    /// <summary>
    /// Gắn cửa sổ chính để có thể mở hộp thoại thêm / sửa tham số.
    /// </summary>
    /// <param name="window">Cửa sổ cha.</param>
    public void Attach(Window? window) => _window = window;

    /// <summary>
    /// Bật webview camera (gọi khi người dùng mở tab Chỉnh PLC lần đầu).
    ///
    /// Khởi tạo WebView2 khá nặng nên chỉ làm khi thật sự cần xem camera.
    /// </summary>
    public void EnableCameraWeb() => IsCameraWebEnabled = true;

    /// <summary>
    /// Bật/tắt bảng tham số + recipe.
    ///
    /// Khi tắt, camera chiếm toàn bộ chiều rộng để xem cho to.
    /// </summary>
    [RelayCommand]
    private void ToggleParameterPanel() => IsParameterPanelVisible = !IsParameterPanelVisible;

    /// <summary>
    /// Tải lại toàn bộ tham số và recipe (KHÔNG đọc giá trị PLC).
    /// </summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await LoadDefinitionsAsync().ConfigureAwait(true);

            SetStatus($"Đã tải {ParameterRows.Count} tham số và {Recipes.Count} recipe.");
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi khi tải dữ liệu PLC: " + ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Tải lại toàn bộ: định nghĩa (tham số + recipe) và giá trị thực tế từ PLC.
    ///
    /// Đây là lệnh được gọi khi mở tab "Chỉnh PLC" và khi người dùng bấm
    /// nút "Cập nhật" — đảm bảo bảng luôn hiển thị giá trị mới nhất.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await LoadDefinitionsAsync().ConfigureAwait(true);
            await ReadAllValuesAsync().ConfigureAwait(true);
            await LoadRecipeValuesIntoInputsAsync().ConfigureAwait(true);

            SetStatus(
                $"Đã cập nhật {ParameterRows.Count} ô nhớ từ PLC và {Recipes.Count} recipe.");
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi khi cập nhật dữ liệu PLC: " + ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Nạp định nghĩa từ DB: danh sách tham số + danh sách recipe.
    /// </summary>
    private async Task LoadDefinitionsAsync()
    {
        var parameters = await _plc.GetParametersAsync().ConfigureAwait(true);

        ParameterRows.Clear();
        foreach (var parameter in parameters)
        {
            ParameterRows.Add(new PlcParamRowViewModel(parameter));
        }

        var recipes = await _plc.GetRecipesAsync().ConfigureAwait(true);

        Recipes.Clear();
        foreach (var recipe in recipes)
        {
            Recipes.Add(recipe);
        }

        OnPropertyChanged(nameof(RecipeCount));

        // Giữ recipe đang chọn nếu vẫn tồn tại, nếu không thì chọn cái đầu.
        if (SelectedRecipe is not null)
        {
            var stillThere = Recipes.FirstOrDefault(r => r.Id == SelectedRecipe.Id);
            SelectedRecipe = stillThere ?? Recipes.FirstOrDefault();
        }
        else
        {
            SelectedRecipe = Recipes.FirstOrDefault();
        }
    }

    /// <summary>
    /// Đọc giá trị thực tế của tất cả ô nhớ từ PLC, điền vào cột "Giá trị".
    /// </summary>
    private async Task ReadAllValuesAsync()
    {
        var results = await _plc.ReadAllAsync().ConfigureAwait(true);

        // Khớp theo ParameterId (không theo chỉ số) để không gán nhầm ô nhớ
        // khi thứ tự trong DB khác thứ tự hiển thị.
        // Dùng vòng lặp ghi đè thay vì ToDictionary để không ném lỗi nếu DB
        // có dữ liệu trùng Id.
        var byId = new Dictionary<long, PlcReadResult>();
        foreach (var item in results)
        {
            byId[item.ParameterId] = item;
        }

        var okCount = 0;
        foreach (var row in ParameterRows)
        {
            if (!byId.TryGetValue(row.Id, out var result))
            {
                continue;
            }

            if (result.IsSuccess)
            {
                row.CurrentValue = result.Value ?? string.Empty;
                row.StatusText = "Đọc OK";
                row.IsError = false;
                okCount++;
            }
            else
            {
                row.CurrentValue = string.Empty;
                row.StatusText = result.Message;
                row.IsError = true;
            }
        }

        SetStatus($"Đọc {okCount}/{results.Count} ô nhớ từ PLC thành công.");

        _actionLog.Log(
            "Plc.Read",
            "PlcParam",
            $"Doc tat ca {results.Count} o nho",
            okCount == results.Count ? UserActionResults.Success : UserActionResults.Failed);
    }

    // ------------------------------------------------------------------
    // CRUD tham so
    // ------------------------------------------------------------------

    /// <summary>
    /// Thêm một dòng tham số mới (chưa lưu) ngay trong bảng.
    ///
    /// Thay vì mở hộp thoại, dòng mới được thêm trực tiếp vào bảng để người
    /// dùng điền tên + ô nhớ ngay tại chỗ, rồi bấm ✓ để lưu.
    /// </summary>
    [RelayCommand]
    private void AddParameter()
    {
        var draft = new NewParamRowViewModel
        {
            IsExpanded = true,
        };

        DraftRows.Insert(0, draft);
    }

    /// <summary>
    /// Lưu dòng đang soạn vào database.
    /// </summary>
    /// <param name="draft">Dòng cần lưu.</param>
    [RelayCommand]
    private async Task SaveDraftParameterAsync(NewParamRowViewModel? draft)
    {
        if (draft is null || draft.IsSaving)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            draft.IsError = true;
            draft.StatusText = "Vui lòng nhập tên tham số.";
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.Address))
        {
            draft.IsError = true;
            draft.StatusText = "Vui lòng nhập ô nhớ (ví dụ D12 hoặc D12.05).";
            return;
        }

        draft.IsSaving = true;
        draft.IsError = false;
        draft.StatusText = string.Empty;

        try
        {
            // Thứ tự sắp xếp: đưa dòng mới lên đầu danh sách.
            var sortOrder = ParameterRows.Count > 0
                ? ParameterRows.Min(r => r.Parameter.SortOrder) - 1
                : 0;

            var parameter = draft.ToParameter(sortOrder);

            var result = await _plc.SaveParameterAsync(parameter).ConfigureAwait(true);

            if (!result.Ok)
            {
                draft.IsError = true;
                draft.StatusText = result.Message;
                return;
            }

            _actionLog.Log(
                "Plc.Param.Create",
                "PlcParam",
                $"Them tham so {parameter.Name} ({parameter.Address})",
                UserActionResults.Success);

            DraftRows.Remove(draft);

            await LoadAsync().ConfigureAwait(true);

            SetStatus($"Đã thêm tham số \"{parameter.Name}\".");
        }
        catch (Exception ex)
        {
            draft.IsError = true;
            draft.StatusText = "Lỗi khi lưu tham số: " + ex.Message;
        }
        finally
        {
            draft.IsSaving = false;
        }
    }

    /// <summary>
    /// Huỷ dòng đang soạn (không lưu).
    /// </summary>
    /// <param name="draft">Dòng cần huỷ.</param>
    [RelayCommand]
    private void CancelDraftParameter(NewParamRowViewModel? draft)
    {
        if (draft is null)
        {
            return;
        }

        DraftRows.Remove(draft);
    }

    /// <summary>
    /// Bật/tắt mở rộng dòng để xem chi tiết (ô nhớ / kiểu / R-W).
    /// </summary>
    /// <param name="row">Dòng cần xổ / thu.</param>
    [RelayCommand]
    private void ToggleRowExpanded(PlcParamRowViewModel? row)
    {
        row?.ToggleExpanded();
    }

    /// <summary>
    /// Bật/tắt mở rộng dòng đang soạn để xem chi tiết.
    /// </summary>
    /// <param name="draft">Dòng cần xổ / thu.</param>
    [RelayCommand]
    private void ToggleDraftExpanded(NewParamRowViewModel? draft)
    {
        draft?.ToggleExpanded();
    }

    /// <summary>
    /// Mở hộp thoại sửa tham số.
    /// </summary>
    /// <param name="row">Dòng cần sửa.</param>
    [RelayCommand]
    private async Task EditParameterAsync(PlcParamRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var saved = await ShowEditDialogAsync(row.Parameter).ConfigureAwait(true);

        if (saved)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Xoá một tham số.
    /// </summary>
    /// <param name="row">Dòng cần xoá.</param>
    [RelayCommand]
    private async Task DeleteParameterAsync(PlcParamRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var result = await _plc.DeleteParameterAsync(row.Id).ConfigureAwait(true);

        SetStatus(result.Message, isError: !result.Ok);

        if (result.Ok)
        {
            _actionLog.Log(
                "Plc.Param.Delete",
                "PlcParam",
                $"Xoa tham so {row.Name} ({row.Address})",
                UserActionResults.Success);

            await LoadAsync().ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------
    // Doc / ghi PLC
    // ------------------------------------------------------------------

    /// <summary>
    /// Đọc giá trị hiện tại của một tham số từ PLC.
    /// </summary>
    /// <param name="row">Dòng cần đọc.</param>
    [RelayCommand]
    private async Task ReadParameterAsync(PlcParamRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var result = await _plc.ReadParameterAsync(row.Parameter).ConfigureAwait(true);

        if (result.IsSuccess)
        {
            row.CurrentValue = result.Value ?? string.Empty;
            row.StatusText = "Đọc OK";
            row.IsError = false;

            _actionLog.Log(
                "Plc.Read",
                "PlcParam",
                $"Doc {row.Name} ({row.Address}) = {row.CurrentValue}",
                UserActionResults.Success);
        }
        else
        {
            row.StatusText = result.Message;
            row.IsError = true;

            _actionLog.Log(
                "Plc.Read",
                "PlcParam",
                $"Doc {row.Name} ({row.Address}) that bai: {result.Message}",
                UserActionResults.Failed);
        }
    }

    /// <summary>
    /// Ghi giá trị đang nhập của một tham số xuống PLC.
    /// </summary>
    /// <param name="row">Dòng cần ghi.</param>
    [RelayCommand]
    private async Task WriteParameterAsync(PlcParamRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var result = await _plc
            .WriteParameterAsync(row.Parameter, row.InputValue)
            .ConfigureAwait(true);

        if (result.IsSuccess)
        {
            row.CurrentValue = result.Value ?? string.Empty;
            row.StatusText = "Ghi OK";
            row.IsError = false;

            _actionLog.Log(
                "Plc.Write",
                "PlcParam",
                $"Ghi {row.Name} ({row.Address}) = {result.Value}",
                UserActionResults.Success);
        }
        else
        {
            row.StatusText = result.Message;
            row.IsError = true;

            _actionLog.Log(
                "Plc.Write",
                "PlcParam",
                $"Ghi {row.Name} ({row.Address}) that bai: {result.Message}",
                UserActionResults.Failed);
        }
    }

    /// <summary>
    /// Đọc tất cả tham số từ PLC (nút "Cập nhật" ở footer bảng ô nhớ).
    /// </summary>
    [RelayCommand]
    private async Task ReadAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await ReadAllValuesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi khi đọc PLC: " + ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Nạp giá trị của recipe đang chọn vào các ô nhập (nút "Tải lên").
    /// </summary>
    [RelayCommand]
    private async Task LoadRecipeValuesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await LoadRecipeValuesIntoInputsAsync().ConfigureAwait(true);

            SetStatus(
                SelectedRecipe is null
                    ? "Chưa chọn recipe nào để tải lên."
                    : $"Đã tải giá trị recipe \"{SelectedRecipe.Name}\" vào ô nhập.");
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi khi tải giá trị recipe: " + ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Áp dụng: ghi giá trị đang nhập xuống PLC, rồi lưu vào recipe (nút "Áp dụng").
    ///
    /// Khác với <c>ApplyRecipeAsync</c> (dùng giá trị đã lưu trong DB),
    /// lệnh này gửi đúng giá trị người đang nhập trên bảng.
    /// </summary>
    [RelayCommand]
    private async Task ApplyAndSaveRecipeAsync()
    {
        if (SelectedRecipe is null)
        {
            SetStatus("Chưa chọn recipe nào.", isError: true);
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            // 1) Gom gia tri nguoi dang nhap.
            var values = new Dictionary<long, string>();
            foreach (var row in ParameterRows)
            {
                if (!string.IsNullOrWhiteSpace(row.InputValue))
                {
                    values[row.Id] = row.InputValue.Trim();
                }
            }

            if (values.Count == 0)
            {
                SetStatus("Chưa nhập giá trị nào để áp dụng.", isError: true);
                return;
            }

            // 2) Ghi xuong PLC.
            var okCount = 0;
            foreach (var row in ParameterRows)
            {
                if (!values.TryGetValue(row.Id, out var value))
                {
                    continue;
                }

                var writeResult = await _plc
                    .WriteParameterAsync(row.Parameter, value)
                    .ConfigureAwait(true);

                if (writeResult.IsSuccess)
                {
                    row.CurrentValue = writeResult.Value ?? string.Empty;
                    row.StatusText = "Ghi OK";
                    row.IsError = false;
                    okCount++;
                }
                else
                {
                    row.StatusText = writeResult.Message;
                    row.IsError = true;
                }
            }

            // 3) Luu bo gia tri vao recipe.
            var saveResult = await _plc
                .SaveRecipeValuesAsync(SelectedRecipe.Id, values)
                .ConfigureAwait(true);

            if (!saveResult.Ok)
            {
                SetStatus(saveResult.Message, isError: true);
            }
            else if (okCount == values.Count)
            {
                SetStatus(
                    $"Đã áp dụng {okCount} ô nhớ xuống PLC và lưu vào recipe \"{SelectedRecipe.Name}\".");
            }
            else
            {
                SetStatus(
                    $"Áp dụng xuống PLC: {okCount}/{values.Count} ô nhớ thành công; "
                    + $"đã lưu vào recipe \"{SelectedRecipe.Name}\".",
                    isError: true);
            }

            _actionLog.Log(
                "Plc.ApplyRecipe",
                "PlcRecipe",
                $"Ap dung gia tri vao PLC va luu recipe {SelectedRecipe.Name}: "
                + $"{okCount}/{values.Count} thanh cong",
                okCount == values.Count ? UserActionResults.Success : UserActionResults.Failed);
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi khi áp dụng: " + ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------
    // Recipe
    // ------------------------------------------------------------------

    /// <summary>
    /// Tạo recipe mới từ tên trong ô nhập.
    /// </summary>
    [RelayCommand]
    private async Task CreateRecipeAsync()
    {
        if (string.IsNullOrWhiteSpace(NewRecipeName))
        {
            SetStatus("Vui lòng nhập tên recipe.", isError: true);
            return;
        }

        var result = await _plc.CreateRecipeAsync(NewRecipeName.Trim()).ConfigureAwait(true);

        SetStatus(result.Message, isError: !result.Ok);

        if (result.Ok)
        {
            NewRecipeName = string.Empty;

            _actionLog.Log(
                "Plc.Recipe.Create",
                "PlcRecipe",
                $"Tao recipe {NewRecipeName}",
                UserActionResults.Success);

            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Xoá recipe đang chọn (yêu cầu xác nhận mật khẩu).
    /// </summary>
    [RelayCommand]
    private async Task DeleteRecipeAsync()
    {
        if (SelectedRecipe is null)
        {
            SetStatus("Chưa chọn recipe nào.", isError: true);
            return;
        }

        var recipe = SelectedRecipe;

        // Bắt buộc xác nhận mật khẩu trước khi xoá.
        var confirmed = await ShowDeleteConfirmAsync(recipe.Name).ConfigureAwait(true);

        if (!confirmed)
        {
            SetStatus("Đã huỷ xoá recipe.");
            return;
        }

        var result = await _plc.DeleteRecipeAsync(recipe.Id).ConfigureAwait(true);

        SetStatus(result.Message, isError: !result.Ok);

        if (result.Ok)
        {
            _actionLog.Log(
                "Plc.Recipe.Delete",
                "PlcRecipe",
                $"Xoa recipe {recipe.Name}",
                UserActionResults.Success);

            SelectedRecipe = null;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Lưu các giá trị đang nhập vào recipe đang chọn.
    /// </summary>
    [RelayCommand]
    private async Task SaveRecipeValuesAsync()
    {
        if (SelectedRecipe is null)
        {
            SetStatus("Chưa chọn recipe nào.", isError: true);
            return;
        }

        var values = new Dictionary<long, string>();
        foreach (var row in ParameterRows)
        {
            if (!string.IsNullOrWhiteSpace(row.InputValue))
            {
                values[row.Id] = row.InputValue.Trim();
            }
        }

        var result = await _plc
            .SaveRecipeValuesAsync(SelectedRecipe.Id, values)
            .ConfigureAwait(true);

        SetStatus(result.Message, isError: !result.Ok);

        if (result.Ok)
        {
            _actionLog.Log(
                "Plc.Recipe.Save",
                "PlcRecipe",
                $"Luu {values.Count} gia tri vao recipe {SelectedRecipe.Name}",
                UserActionResults.Success);
        }
    }

    /// <summary>
    /// Áp dụng giá trị của recipe đang chọn xuống PLC.
    /// </summary>
    [RelayCommand]
    private async Task ApplyRecipeAsync()
    {
        if (SelectedRecipe is null)
        {
            SetStatus("Chưa chọn recipe nào.", isError: true);
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var results = await _plc.ApplyRecipeAsync(SelectedRecipe.Id).ConfigureAwait(true);

            var okCount = results.Count(r => r.IsSuccess);

            foreach (var result in results)
            {
                if (!result.IsSuccess)
                {
                    SetStatus(result.Message, isError: true);
                }
            }

            if (okCount == results.Count)
            {
                SetStatus(
                    $"Đã áp dụng recipe \"{SelectedRecipe.Name}\": {okCount} ô nhớ ghi thành công.");
            }
            else
            {
                SetStatus(
                    $"Áp dụng recipe \"{SelectedRecipe.Name}\": {okCount}/{results.Count} ô nhớ thành công.",
                    isError: true);
            }

            _actionLog.Log(
                "Plc.ApplyRecipe",
                "PlcRecipe",
                $"Ap dung recipe {SelectedRecipe.Name}: {okCount}/{results.Count} thanh cong",
                okCount == results.Count ? UserActionResults.Success : UserActionResults.Failed);
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi khi áp dụng recipe: " + ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Nạp giá trị của recipe đang chọn vào các ô nhập.
    /// </summary>
    private async Task LoadRecipeValuesIntoInputsAsync()
    {
        foreach (var row in ParameterRows)
        {
            row.InputValue = string.Empty;
        }

        if (SelectedRecipe is null)
        {
            return;
        }

        try
        {
            var values = await _plc
                .GetRecipeValuesAsync(SelectedRecipe.Id)
                .ConfigureAwait(true);

            foreach (var row in ParameterRows)
            {
                if (values.TryGetValue(row.Id, out var value))
                {
                    row.InputValue = value;
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi khi tải giá trị recipe: " + ex.Message, isError: true);
        }
    }

    /// <summary>
    /// Xử lý khi người dùng chọn recipe khác — nạp lại giá trị.
    /// </summary>
    /// <param name="value">Recipe mới được chọn.</param>
    partial void OnSelectedRecipeChanged(PlcRecipe? value)
    {
        if (value is null)
        {
            return;
        }

        // Fire-and-forget: nạp giá trị recipe vào các ô nhập.
        _ = LoadRecipeValuesIntoInputsAsync();
    }

    // ------------------------------------------------------------------
    // Dialog
    // ------------------------------------------------------------------

    /// <summary>
    /// Mở hộp thoại xác nhận xoá recipe bằng mật khẩu.
    /// </summary>
    /// <param name="recipeName">Tên recipe sắp xoá.</param>
    /// <returns>True nếu người dùng xác nhận đúng mật khẩu.</returns>
    private async Task<bool> ShowDeleteConfirmAsync(string recipeName)
    {
        var owner = _window;

        // Owner phải đã hiển thị mới mở được dialog.
        if (owner is null || !owner.IsVisible)
        {
            SetStatus("Không mở được hộp thoại: cửa sổ chưa sẵn sàng.", isError: true);
            return false;
        }

        var vm = new RecipeDeleteConfirmViewModel(_users, _session, recipeName);
        var dialog = new RecipeDeleteConfirmWindow
        {
            DataContext = vm,
        };

        try
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            return dialog.IsConfirmed;
        }
        catch (InvalidOperationException ex)
        {
            SetStatus("Không mở được hộp thoại: " + ex.Message, isError: true);
            return false;
        }
    }

    /// <summary>
    /// Mở hộp thoại thêm / sửa tham số.
    /// </summary>
    /// <param name="parameter">Tham số đang sửa (null = tạo mới).</param>
    /// <returns>True nếu lưu thành công.</returns>
    private async Task<bool> ShowEditDialogAsync(PlcParameter? parameter)
    {
        var owner = _window;

        // Owner phải đã hiển thị mới mở được dialog.
        if (owner is null || !owner.IsVisible)
        {
            SetStatus("Không mở được hộp thoại: cửa sổ chưa sẵn sàng.", isError: true);
            return false;
        }

        var vm = new PlcParamEditViewModel(_plc, parameter);
        var dialog = new PlcParamEditWindow
        {
            DataContext = vm,
        };

        try
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            return dialog.IsSaved;
        }
        catch (InvalidOperationException ex)
        {
            SetStatus("Không mở được hộp thoại: " + ex.Message, isError: true);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Helper
    // ------------------------------------------------------------------

    /// <summary>
    /// Dựng URI webview camera từ IP cấu hình.
    /// </summary>
    private Uri BuildCameraUri()
    {
        var ip = _cameraOptions.Ip?.Trim();

        if (string.IsNullOrWhiteSpace(ip))
        {
            return new Uri("about:blank");
        }

        // Bỏ scheme nếu người dùng đã ghi kèm (vd "http://192.168.1.5").
        if (ip.Contains("://", StringComparison.Ordinal))
        {
            ip = ip.Split("://", 2)[1];
        }

        ip = ip.TrimEnd('/');

        var url = $"http://{ip}/monitor";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : new Uri("about:blank");
    }

    /// <summary>
    /// Đặt thông báo trạng thái.
    /// </summary>
    /// <param name="message">Nội dung.</param>
    /// <param name="isError">True nếu là lỗi.</param>
    private void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }
}

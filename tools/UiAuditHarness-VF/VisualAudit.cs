using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace UiAuditHarness;

/// <summary>Một node trong dump visual tree.</summary>
internal sealed class NodeInfo
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Text { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double DesiredW { get; set; }
    public double DesiredH { get; set; }
    public bool IsVisible { get; set; }
    public double MarginL { get; set; }
    public double MarginT { get; set; }
    public double MarginR { get; set; }
    public double MarginB { get; set; }
    public double PaddingL { get; set; }
    public double PaddingT { get; set; }
    public double PaddingR { get; set; }
    public double PaddingB { get; set; }
    public double? FixedHeight { get; set; }
    public double? FixedWidth { get; set; }
    public string? Clip { get; set; }
    public string? FontSize { get; set; }
    public bool TextWrapping { get; set; }
    public int? MaxLines { get; set; }
    public string? Trimming { get; set; }
    public int Depth { get; set; }

    /// <summary>Chỉ số hàng trong Grid cha (-1 = không phải Grid).</summary>
    public int GridRow { get; set; } = -1;

    /// <summary>Chỉ số cột trong Grid cha (-1 = không phải Grid).</summary>
    public int GridCol { get; set; } = -1;

    /// <summary>True nếu nằm trong template của Popup / dropdown (không tính layout chính).</summary>
    public bool InPopup { get; set; }

    /// <summary>True nếu nằm trong vùng cuộn (ScrollViewer) — có thể dài hơn khung nhìn.</summary>
    public bool InScroll { get; set; }
}

/// <summary>Một phát hiện của kiểm tra tự động.</summary>
internal sealed class Finding
{
    public string View { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Severity { get; set; } = "Minor";
    public string Element { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string? Screenshot { get; set; }
}

/// <summary>
/// Dump visual tree + chạy các kiểm tra bố cục tự động.
///
/// Mọi phép so sánh dùng tọa độ TƯƠNG ĐỐI so với cửa sổ (đã trừ origin),
/// nên con số trong báo cáo là pixel thật trong ảnh PNG.
/// </summary>
internal static class VisualAudit
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Chuẩn hoá số vô hạn/NaN thành 0 (JSON không nhận).</summary>
    private static double N(double v) =>
        double.IsFinite(v) ? Math.Round(v, 2) : 0d;

    // ==================================================================
    // Dump
    // ==================================================================

    /// <summary>Dump cây + lưu JSON.</summary>
    public static List<NodeInfo> Dump(Window win, string fileStem)
    {
        var nodes = new List<NodeInfo>();
        var winSize = win.ClientSize;

        foreach (var child in win.GetVisualChildren())
        {
            if (child is Control cc)
            {
                var name = !string.IsNullOrEmpty(cc.Name) ? cc.Name : cc.GetType().Name;
                Walk(cc, "/" + Sanitize(name), 0, nodes, win, false, false);
            }
        }

        var jsonPath = Path.Combine(Sandbox.DumpRoot, fileStem + ".tree.json");
        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(
                new
                {
                    window = win.GetType().Name,
                    title = win.Title,
                    clientWidth = N(winSize.Width),
                    clientHeight = N(winSize.Height),
                    pixelSize = new
                    {
                        w = (int)Math.Max(0, Math.Min(int.MaxValue, winSize.Width)),
                        h = (int)Math.Max(0, Math.Min(int.MaxValue, winSize.Height)),
                    },
                    nodes,
                },
                JsonOpts));

        return nodes;
    }

    private static void Walk(
        Control c, string path, int depth, List<NodeInfo> sink, Window w, bool inPopup, bool inScroll)
    {
        var bounds = c.Bounds;
        var origin = c.TranslatePoint(new Point(0, 0), w) ?? new Point(0, 0);

        var n = new NodeInfo
        {
            Path = path,
            Name = c.Name ?? string.Empty,
            Type = c.GetType().Name,
            Text = (c as TextBlock)?.Text,
            X = N(origin.X),
            Y = N(origin.Y),
            W = N(bounds.Width),
            H = N(bounds.Height),
            DesiredW = N(c.DesiredSize.Width),
            DesiredH = N(c.DesiredSize.Height),
            IsVisible = c.IsVisible,
            MarginL = N(c.Margin.Left),
            MarginT = N(c.Margin.Top),
            MarginR = N(c.Margin.Right),
            MarginB = N(c.Margin.Bottom),
            PaddingL = N(GetPadding(c, "Left")),
            PaddingT = N(GetPadding(c, "Top")),
            PaddingR = N(GetPadding(c, "Right")),
            PaddingB = N(GetPadding(c, "Bottom")),
            FixedHeight = GetFixed(Layoutable.HeightProperty, c),
            FixedWidth = GetFixed(Layoutable.WidthProperty, c),
            Depth = depth,
            InPopup = inPopup,
            InScroll = inScroll,
        };

        var clipInfo = DescribeClip(c.Clip);
        if (clipInfo is not null)
        {
            n.Clip = clipInfo;
        }

        if (c is TextBlock tb)
        {
            n.FontSize = tb.FontSize.ToString(CultureInfo.InvariantCulture);
            n.TextWrapping = tb.TextWrapping == Avalonia.Media.TextWrapping.Wrap;
            n.MaxLines = tb.MaxLines;
            n.Trimming = tb.TextTrimming?.ToString();
        }
        else if (c is TextBox tbox)
        {
            n.Text = n.Text ?? tbox.Text;
        }
        else if (c is SelectableTextBlock stb)
        {
            n.Text = n.Text ?? stb.Text;
        }

        // Vị trí ô trong Grid cha (nếu có).
        if (c.Parent is Grid pg)
        {
            n.GridRow = GridIndexOf(pg.RowDefinitions.Count, Grid.GetRow(c));
            n.GridCol = GridIndexOf(pg.ColumnDefinitions.Count, Grid.GetColumn(c));
        }

        sink.Add(n);

        var idx = 0;
        foreach (var child in c.GetVisualChildren())
        {
            if (child is Control cc)
            {
                var name = !string.IsNullOrEmpty(cc.Name)
                    ? cc.Name
                    : $"{cc.GetType().Name}[{idx}]";
                Walk(
                    cc,
                    path + "/" + Sanitize(name),
                    depth + 1,
                    sink,
                    w,
                    inPopup || IsPopupLike(cc),
                    inScroll || cc is ScrollViewer);
            }

            idx++;
        }
    }

    /// <summary>ComboBox / Popup / Menu: template nằm ngoài layout chính.</summary>
    private static bool IsPopupLike(Control c) =>
        c is Popup
        || c is ComboBox
        || c is Menu
        || c is ContextMenu;

    private static int GridIndexOf(int count, int absolute)
    {
        if (count <= 0)
        {
            return -1;
        }

        // Avalonia trả -1 khi thuộc tính chưa được đặt -> mặc định là hàng/cột 0.
        if (absolute < 0)
        {
            return 0;
        }

        return absolute >= count ? count - 1 : absolute;
    }

    /// <summary>
    /// Mô tả clip của một control (Avalonia 12 đổi tên RectangleClip ->
    /// dùng reflection cho an toàn).
    /// </summary>
    private static string? DescribeClip(Geometry? clip)
    {
        if (clip is null)
        {
            return null;
        }

        var type = clip.GetType();
        var rectProp = type.GetProperty("Rect");
        if (rectProp?.GetValue(clip) is Rect r)
        {
            return $"{type.Name}({r.X},{r.Y},{r.Width},{r.Height})";
        }

        var bounds = clip.Bounds;
        return $"{type.Name}(bounds {bounds.X},{bounds.Y},{bounds.Width},{bounds.Height})";
    }

    private static double GetPadding(Control c, string side)
    {
        var p = c switch
        {
            ContentControl cc => cc.Padding,
            Border b => b.Padding,
            Decorator d => d.Padding,
            _ => default,
        };

        return side switch
        {
            "Left" => p.Left,
            "Top" => p.Top,
            "Right" => p.Right,
            _ => p.Bottom,
        };
    }

    private static double? GetFixed(AvaloniaProperty<double> prop, Control c)
    {
        var v = c.GetValue(prop);
        if (v is Avalonia.Data.BindingExpressionBase)
        {
            return null;
        }

        if (v is not double d)
        {
            return null;
        }

        return d <= 0 ? null : d;
    }

    private static string Sanitize(string s) =>
        s.Replace('\\', '/').Replace('[', '(').Replace(']', ')').Replace(' ', '_');

    // ==================================================================
    // Kiểm tra tự động
    // ==================================================================

    /// <summary>Chạy toàn bộ kiểm tra bố cục cho một view.</summary>
    public static List<Finding> Check(string viewName, List<NodeInfo> nodes, double winW, double winH)
    {
        var findings = new List<Finding>();

        // Chỉ xét node thực sự hiện ra: visible, có kích thước, không nằm trong popup.
        var visible = nodes
            .Where(n => n.IsVisible && n.W > 0 && n.H > 0 && !n.InPopup)
            .ToList();

        // Element trong vùng cuộn được phép dài hơn khung nhìn -> chỉ kiểm
        // tra chiều ngang (tràn trái/phải), bỏ qua chiều dọc.
        var notScrolled = visible.Where(n => !n.InScroll).ToList();

        var byPath = nodes.ToDictionary(n => n.Path, n => n);

        // ---- 1. Element nằm ngoài cửa sổ ----
        foreach (var n in notScrolled)
        {
            var overR = n.X + n.W - winW;
            var overB = n.Y + n.H - winH;
            if (overR > 2.0 || overB > 2.0 || n.X < -2.0 || n.Y < -2.0)
            {
                findings.Add(new Finding
                {
                    View = viewName,
                    Kind = "outside-window",
                    Severity = "Major",
                    Element = n.Path,
                    Detail =
                        $"Element tràn ra ngoài vùng cửa sổ. X={n.X} Y={n.Y} W={n.W} H={n.H}; " +
                        $"tràn phải={Math.Round(overR, 1)}px, tràn dưới={Math.Round(overB, 1)}px " +
                        $"(cửa sổ {winW}x{winH}). Text=\"{Shorten(n.Text, 40)}\"",
                });
            }
        }

        // ---- 1b. Element trong vùng cuộn tràn ra ngoài cửa sổ theo chiều ngang ----
        foreach (var n in visible.Where(x => x.InScroll))
        {
            var overR = n.X + n.W - winW;
            if (overR > 2.0 || n.X < -2.0)
            {
                findings.Add(new Finding
                {
                    View = viewName,
                    Kind = "outside-window-horizontal",
                    Severity = "Major",
                    Element = n.Path,
                    Detail =
                        $"Nội dung cuộn tràn ngang ra ngoài cửa sổ. X={n.X} Y={n.Y} W={n.W}; " +
                        $"tràn phải={Math.Round(overR, 1)}px, tràn trái={Math.Round(-n.X, 1)}px " +
                        $"(cửa sổ rộng {winW}px). Text=\"{Shorten(n.Text, 40)}\"",
                });
            }
        }

        // ---- 2. TextBlock bị cắt chữ ----
        foreach (var n in visible)
        {
            if (n.Type != "TextBlock" || n.TextWrapping)
            {
                continue;
            }

            var overflowX = n.DesiredW - n.W;
            var overflowY = n.DesiredH - n.H;
            if (overflowX > 2.0 || overflowY > 2.0)
            {
                findings.Add(new Finding
                {
                    View = viewName,
                    Kind = "text-truncated",
                    Severity = overflowX > 8 || overflowY > 8 ? "Major" : "Minor",
                    Element = n.Path,
                    Detail =
                        $"TextBlock bị cắt: DesiredSize={n.DesiredW}x{n.DesiredH} vs Bounds={n.W}x{n.H} " +
                        $"(thiếu {Math.Round(overflowX, 1)}px ngang / {Math.Round(overflowY, 1)}px dọc). " +
                        $"Text=\"{Shorten(n.Text)}\" Trimming={n.Trimming ?? "none"} " +
                        $"MaxLines={n.MaxLines?.ToString() ?? "null"} FontSize={n.FontSize ?? "?"}.",
                });
            }
        }

        // ---- 3. Con tràn ra ngoài ô của cha ----
        foreach (var n in notScrolled)
        {
            var parentPath = ParentOf(n.Path);
            if (parentPath is null || !byPath.TryGetValue(parentPath, out var parent))
            {
                continue;
            }

            if (!parent.IsVisible || parent.InPopup || parent.InScroll)
            {
                continue;
            }

            // Chỉ container thực sự cắt nội dung mới đáng kiểm tra.
            if (!IsClippingContainer(parent.Type))
            {
                continue;
            }

            var right = n.X + n.W;
            var bottom = n.Y + n.H;
            var pRight = parent.X + parent.W;
            var pBottom = parent.Y + parent.H;
            const double eps = 1.5;

            if (right > pRight + eps || bottom > pBottom + eps)
            {
                var overR = Math.Round(right - pRight, 1);
                var overB = Math.Round(bottom - pBottom, 1);

                findings.Add(new Finding
                {
                    View = viewName,
                    Kind = "child-overflows-parent",
                    Severity = "Major",
                    Element = n.Path,
                    Detail =
                        $"Element tràn ra ngoài ô của cha ({parent.Type}). " +
                        $"Con: X={n.X} Y={n.Y} W={n.W} H={n.H} → phải={right}, dưới={bottom}. " +
                        $"Cha: X={parent.X} Y={parent.Y} W={parent.W} H={parent.H} " +
                        $"→ phải={pRight}, dưới={pBottom}. " +
                        $"Tràn ngang={overR}px, tràn dọc={overB}px.",
                });
            }
        }

        // ---- 4/5. Ô cùng hàng / cùng cột trong Grid: lệch cao / lệch mép ----
        foreach (var gridGroup in nodes
            .Where(n => n.IsVisible && !n.InPopup && !n.InScroll && n.GridRow >= 0)
            .GroupBy(n => ParentOf(n.Path)))
        {
            if (gridGroup.Key is null || !byPath.TryGetValue(gridGroup.Key, out var grid))
            {
                continue;
            }

            if (!grid.Type.EndsWith("Grid", StringComparison.Ordinal))
            {
                continue;
            }

            // Nhóm theo (hàng) để so sánh các ô nằm cùng hàng.
            foreach (var row in gridGroup.GroupBy(n => n.GridRow))
            {
                var cells = row.OrderBy(n => n.GridCol).ThenBy(n => n.X).ToList();
                if (cells.Count < 2)
                {
                    continue;
                }

                var heights = cells.Select(c => c.H).Distinct().ToList();
                if (heights.Count > 1)
                {
                    var min = heights.Min();
                    var max = heights.Max();
                    if (max - min > 3.0)
                    {
                        var desc = string.Join(
                            ", ",
                            cells.Select(c => $"{Shorten(c.Path, 44)}@col={c.GridCol}:h={c.H}"));
                        findings.Add(new Finding
                        {
                            View = viewName,
                            Kind = "sibling-height-mismatch",
                            Severity = max - min > 12 ? "Major" : "Minor",
                            Element = grid.Path,
                            Detail =
                                $"Các ô cùng hàng {row.Key} trong Grid lệch chiều cao " +
                                $"(max {max} vs min {min}, chênh {Math.Round(max - min, 1)}px). " + desc,
                        });
                    }
                }

                // Mép trái các ô trong cùng hàng phải thẳng nhau.
                var tops = cells.Select(c => c.Y).Distinct().ToList();
                if (tops.Count > 1)
                {
                    var minT = tops.Min();
                    var maxT = tops.Max();
                    if (maxT - minT > 3.0)
                    {
                        var desc = string.Join(
                            ", ",
                            cells.Select(c => $"{Shorten(c.Path, 44)}@col={c.GridCol}:y={c.Y}"));
                        findings.Add(new Finding
                        {
                            View = viewName,
                            Kind = "sibling-y-misaligned",
                            Severity = "Minor",
                            Element = grid.Path,
                            Detail =
                                $"Các ô cùng hàng {row.Key} lệch mép trên " +
                                $"(y từ {minT} đến {maxT}, chênh {Math.Round(maxT - minT, 1)}px). " + desc,
                        });
                    }
                }
            }

            // Nhóm theo (cột) để so sánh các ô nằm cùng cột.
            foreach (var col in gridGroup.GroupBy(n => n.GridCol))
            {
                var cells = col.OrderBy(n => n.GridRow).ThenBy(n => n.Y).ToList();
                if (cells.Count < 2)
                {
                    continue;
                }

                var widths = cells.Select(c => c.W).Distinct().ToList();
                if (widths.Count > 1)
                {
                    var min = widths.Min();
                    var max = widths.Max();
                    if (max - min > 3.0)
                    {
                        var desc = string.Join(
                            ", ",
                            cells.Select(c => $"{Shorten(c.Path, 44)}@row={c.GridRow}:w={c.W}"));
                        findings.Add(new Finding
                        {
                            View = viewName,
                            Kind = "sibling-width-mismatch",
                            Severity = max - min > 12 ? "Major" : "Minor",
                            Element = grid.Path,
                            Detail =
                                $"Các ô cùng cột {col.Key} trong Grid lệch chiều rộng " +
                                $"(max {max} vs min {min}, chênh {Math.Round(max - min, 1)}px). " + desc,
                        });
                    }
                }
            }
        }

        // ---- 6. Margin/Padding không nhất quán trong container lặp lại ----
        foreach (var group in visible
            .GroupBy(n => ParentOf(n.Path))
            .Where(g => g.Key is not null && g.Count() >= 2))
        {
            var items = group.ToList();
            var parentPath = group.Key!;
            if (!byPath.TryGetValue(parentPath, out var parent))
            {
                continue;
            }

            if (!parent.Type.Contains("ItemsControl", StringComparison.Ordinal)
                && !parent.Type.Contains("Virtualizing", StringComparison.Ordinal))
            {
                continue;
            }

            var margins = items
                .Select(i => $"{i.MarginL},{i.MarginT},{i.MarginR},{i.MarginB}")
                .Distinct()
                .ToList();
            if (margins.Count > 1)
            {
                findings.Add(new Finding
                {
                    View = viewName,
                    Kind = "inconsistent-item-margin",
                    Severity = "Cosmetic",
                    Element = parentPath,
                    Detail =
                        "Các item lặp lại trong cùng ItemsControl có Margin khác nhau: " +
                        string.Join(" | ", margins) + ".",
                });
            }

            var paddings = items
                .Select(i => $"{i.PaddingL},{i.PaddingT},{i.PaddingR},{i.PaddingB}")
                .Distinct()
                .ToList();
            if (paddings.Count > 1)
            {
                findings.Add(new Finding
                {
                    View = viewName,
                    Kind = "inconsistent-item-padding",
                    Severity = "Cosmetic",
                    Element = parentPath,
                    Detail =
                        "Các item lặp lại trong cùng ItemsControl có Padding khác nhau: " +
                        string.Join(" | ", paddings) + ".",
                });
            }
        }

        // ---- 7. Fixed Height/Width quá nhỏ so với DesiredSize ----
        foreach (var n in notScrolled)
        {
            if (n.FixedHeight is { } fh && n.DesiredH > fh + 2.0)
            {
                findings.Add(new Finding
                {
                    View = viewName,
                    Kind = "fixed-height-too-small",
                    Severity = "Minor",
                    Element = n.Path,
                    Detail =
                        $"Height cố định {fh}px nhỏ hơn DesiredSize.Height {n.DesiredH}px " +
                        $"(thiếu {Math.Round(n.DesiredH - fh, 1)}px) → nội dung bị cắt. " +
                        $"Type={n.Type} Text=\"{Shorten(n.Text, 30)}\"",
                });
            }

            if (n.FixedWidth is { } fw && n.DesiredW > fw + 2.0 && !n.TextWrapping)
            {
                findings.Add(new Finding
                {
                    View = viewName,
                    Kind = "fixed-width-too-small",
                    Severity = "Minor",
                    Element = n.Path,
                    Detail =
                        $"Width cố định {fw}px nhỏ hơn DesiredSize.Width {n.DesiredW}px " +
                        $"(thiếu {Math.Round(n.DesiredW - fw, 1)}px) → nội dung bị cắt. " +
                        $"Type={n.Type} Text=\"{Shorten(n.Text, 30)}\"",
                });
            }
        }

        // ---- 8. Vùng rỗng bất thường ----
        foreach (var n in visible)
        {
            if (n.H >= 40 && n.DesiredH <= 1.0 && n.Type.EndsWith("StackPanel", StringComparison.Ordinal))
            {
                findings.Add(new Finding
                {
                    View = viewName,
                    Kind = "empty-container",
                    Severity = "Cosmetic",
                    Element = n.Path,
                    Detail =
                        $"StackPanel rỗng nhưng chiếm {n.W}x{n.H}px — khoảng trống thừa trong layout.",
                });
            }
        }

        return findings;
    }

    /// <summary>
    /// Container có thể cắt nội dung con (không tính ScrollViewer — nội dung
    /// con được phép dài hơn vì cuộn được).
    /// </summary>
    private static bool IsClippingContainer(string type) =>
        type.EndsWith("Grid", StringComparison.Ordinal)
        || type.EndsWith("Border", StringComparison.Ordinal)
        || type.EndsWith("ContentControl", StringComparison.Ordinal)
        || type.EndsWith("Decorator", StringComparison.Ordinal);

    private static string? ParentOf(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? null : path[..idx];
    }

    private static string Shorten(string? s, int max = 60)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "(null)";
        }

        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }
}

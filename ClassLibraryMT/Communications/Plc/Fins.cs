namespace ClassLibraryMT.Communications.Plc;

/// <summary>Hang so giao thuc FINS/UDP cua Omron.</summary>
internal static class Fins
{
    /// <summary>Do dai header FINS (10 byte).</summary>
    public const int HeaderLength = 10;

    /// <summary>
    /// Do dai than lenh Memory Area Read/Write:
    /// MRC(1) + SRC(1) + area(1) + dia chi(2) + bit(1) + so word(2) = 8 byte.
    /// </summary>
    public const int CommandLength = 8;

    /// <summary>ICF: command 0x80 (request), 0xC0 (response).</summary>
    public const byte IcfRequest = 0x80;
    public const byte IcfResponse = 0xC0;

    /// <summary>GCT: gateway count.</summary>
    public const byte Gct = 0x02;

    /// <summary>DNA/DA1: dia chi node dich.</summary>
    public const byte Dna = 0x00;

    /// <summary>SNA/SA1: dia chi node nguon.</summary>
    public const byte Sna = 0x00;

    /// <summary>MRC/SRC: Memory Area Read / Write.</summary>
    public const byte MrcMemoryArea = 0x01;
    public const byte SrcRead = 0x01;
    public const byte SrcWrite = 0x02;

    /// <summary>Ma vung D (DM) trong Memory Area Read/Write.</summary>
    public const byte AreaD = 0x82;

    /// <summary>Gia tri SID bat dau tu 0, tang dan 1 moi request.</summary>
    public const byte SidStart = 0x00;

    /// <summary>
    /// So word toi da doc/ghi trong mot request FINS.
    ///
    /// Gia tri 500 khop voi <c>OmronFinsNet.ReadSplits</c> mac dinh cua HslCommunication,
    /// nen frame giong het phia server.
    /// </summary>
    public const int MaxWordsPerRequest = 500;

    /// <summary>
    /// Ma loi FINS thuong gap, chuyen sang thong diep de doc.
    /// </summary>
    public static string DescribeEndCode(ushort endCode) => endCode switch
    {
        0x0000 => "Thành công.",
        0x0001 => "Service không được hoàn thành (bị hủy).",
        0x0002 => "Không phải lỗi dịch vụ.",
        0x0011 => "Không thể phục vụ, máy đích đang chạy hoặc không được thao tác.",
        0x0101 => "Địa chỉ không tồn tại.",
        0x0102 => "Địa chỉ đã được gán giá trị.",
        0x0103 => "Thông số không đúng.",
        0x0104 => "Chỉ số ngoài phạm vi.",
        0x0105 => "Địa chỉ bắt đầu + số lượng vượt quá giới hạn vùng.",
        0x0201 => "Địa chỉ không tồn tại.",
        0x0202 => "Thông số không đúng.",
        0x0203 => "Chỉ số ngoài phạm vi.",
        0x0204 => "Địa chỉ bắt đầu + số lượng vượt quá giới hạn vùng.",
        0x0401 => "Lệnh không được hỗ trợ.",
        0x0402 => "Không thể phục vụ do mô hình dịch vụ không hỗ trợ lệnh này.",
        0x0403 => "Thông số không đúng.",
        0x1101 => "Địa chỉ bắt đầu + số lượng vượt quá giới hạn vùng.",
        0x1102 => "Số lượng dữ liệu được chỉ định vượt quá giới hạn.",
        0x1103 => "Chỉ định số lượng dữ liệu không đúng.",
        0x1104 => "Thông số không đúng.",
        0x1105 => "Có byte thông báo.",
        0x1106 => "Loại dữ liệu không đúng.",
        0x1107 => "Độ dài dữ liệu vượt quá giới hạn.",
        0x1108 => "Thông số không đúng.",
        0x1109 => "Loại dữ liệu không đúng.",
        0x110A => "Thông số không đúng.",
        0x110B => "Có byte thông báo.",
        0x110C => "Có byte thông báo.",
        0x2002 => "Thông số không đúng.",
        0x2003 => "Không thể giao tiếp với máy đích.",
        0x2004 => "Không thể giao tiếp với máy đích.",
        0x2010 => "Địa chỉ máy không tồn tại.",
        0x3011 => "Hết thời gian chờ lệnh ghi vào bộ đệm giao tiếp.",
        0x3012 => "Hết thời gian chờ lệnh đọc từ bộ đệm giao tiếp.",
        _ => $"End code FINS 0x{endCode:X4}.",
    };
}

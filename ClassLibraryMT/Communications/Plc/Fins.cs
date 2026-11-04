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
        0x0000 => "Thanh cong.",
        0x0001 => "Service khong duoc hoan thanh (bi huy).",
        0x0002 => "Khong phai loi dich vu.",
        0x0011 => "Khong the phuc vu, may dich dang chay hoac khong duoc thao tac.",
        0x0101 => "Dia chi khong ton tai.",
        0x0102 => "Dia chi da duoc gan gia tri.",
        0x0103 => "Thong so khong dung.",
        0x0104 => "Chi so ngoai pham vi.",
        0x0105 => "Dia chi bat dau + so luong vuot qua gioi han vung.",
        0x0201 => "Dia chi khong ton tai.",
        0x0202 => "Thong so khong dung.",
        0x0203 => "Chi so ngoai pham vi.",
        0x0204 => "Dia chi bat dau + so luong vuot qua gioi han vung.",
        0x0401 => "Lenh khong duoc ho tro.",
        0x0402 => "Khong the phuc vu do mo hinh dich vu khong ho tro lenh nay.",
        0x0403 => "Thong so khong dung.",
        0x1101 => "Dia chi bat dau + so luong vuot qua gioi han vung.",
        0x1102 => "So luong du lieu duoc chi dinh vuot qua gioi han.",
        0x1103 => "Chi dinh so luong du lieu khong dung.",
        0x1104 => "Thong so khong dung.",
        0x1105 => "Co byte thong bao.",
        0x1106 => "Loai du lieu khong dung.",
        0x1107 => "Do dai du lieu vuot qua gioi han.",
        0x1108 => "Thong so khong dung.",
        0x1109 => "Loai du lieu khong dung.",
        0x110A => "Thong so khong dung.",
        0x110B => "Co byte thong bao.",
        0x110C => "Co byte thong bao.",
        0x2002 => "Thong so khong dung.",
        0x2003 => "Khong the giao tiep voi may dich.",
        0x2004 => "Khong the giao tiep voi may dich.",
        0x2010 => "Dia chi may khong ton tai.",
        0x3011 => "Het thoi gian cho lenh ghi vao bo dem giao tiep.",
        0x3012 => "Het thoi gian cho lenh doc tu bo dem giao tiep.",
        _ => $"End code FINS 0x{endCode:X4}.",
    };
}

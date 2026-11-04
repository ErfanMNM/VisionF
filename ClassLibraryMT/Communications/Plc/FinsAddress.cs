namespace ClassLibraryMT.Communications.Plc;

/// <summary>
/// Dia chi vung D (vung DM) cua PLC Omron.
///
/// Chuan hoa tu chuoi nhu "D16", "DM16", "D0016" (word) hoac "D12.05" (bit).
/// Vung D hop le tu 0 den 32767.
/// </summary>
internal readonly struct FinsAddress
{
    /// <summary>Dia chi word (tu 0 den 32767).</summary>
    public int WordAddress { get; }

    /// <summary>Chi so bit trong word (0-15), hoac null neu la dia chi word thuan.</summary>
    public int? BitAddress { get; }

    /// <summary>True neu dia chi chi dinh den 1 bit cu the trong word.</summary>
    public bool IsBit => BitAddress.HasValue;

    public FinsAddress(int wordAddress, int? bitAddress = null)
    {
        WordAddress = wordAddress;
        BitAddress = bitAddress;
    }

    /// <summary>Vung D chay tu 0 den 32767.</summary>
    public const int MaxWordAddress = 32767;

    /// <summary>
    /// Parse dia chi. Tra false neu sai dinh dang hoac ngoai pham vi
    /// (diem loi se gan ma "ARG").
    /// </summary>
    public static bool TryParse(string? address, out FinsAddress result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(address)) return false;

        var text = address.Trim();

        // Bo tien to "DM" hoac "D" (khong phan biet hoa thuong).
        if (text.StartsWith("DM", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        else if (text.StartsWith("D", StringComparison.OrdinalIgnoreCase))
            text = text[1..];
        else
            return false;

        if (text.Length == 0) return false;

        int? bit = null;

        // Tach phan bit sau dau cham: "12.05" -> word 12, bit 5.
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            var bitPart = text[(dot + 1)..];
            text = text[..dot];

            if (!int.TryParse(bitPart, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var bitValue))
                return false;
            if (bitValue < 0 || bitValue > 15) return false;
            bit = bitValue;
        }

        if (!int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var word))
            return false;

        if (word < 0 || word > MaxWordAddress) return false;

        result = new FinsAddress(word, bit);
        return true;
    }

    /// <summary>
    /// Kiem tra so word doc/ghi khong tran qua dia chi cuoi vung D.
    /// </summary>
    public bool FitsWordCount(int wordCount)
    {
        if (wordCount <= 0) return false;
        var last = (long)WordAddress + wordCount - 1;
        return last <= MaxWordAddress;
    }
}

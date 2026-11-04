using System.Buffers.Binary;

namespace ClassLibraryMT.Communications.Plc;

/// <summary>
/// Chuyen doi du lieu giua kieu .NET va word cua PLC Omron.
///
/// Quy uoc tuong duong <c>ByteTransform.DataFormat = CDAB</c> cua HslCommunication.
///
/// CDAB dao thu tu các block 16-bit cua dang big-endian, tuc la trong FINS
/// (word luon luon big-endian) thi <c>word[0]</c> la 16 bit thap nhat:
///
///   int32 0x12345678 -> byte Hsl (CDAB) = 56 78 12 34
///                     -> word cua PLC    = [0x5678, 0x1234]
///                     (word[0] = 0x5678 = 16 bit thap, word[1] = 0x1234 = 16 bit cao)
///
///   int64 0x1122334455667788 -> word cua PLC = [0x7788, 0x5566, 0x3344, 0x1122]
///
/// Voi 16 bit (short/ushort) thi chi co 1 word, khong co dao doi.
/// </summary>
internal static class FinsData
{
    // ------------------------------------------------------------------
    // Doc (word -> gia tri)
    // ------------------------------------------------------------------

    /// <summary>Doc mot word va tra ve gia tri 16-bit co dau.</summary>
    public static short ToInt16(ReadOnlySpan<ushort> words, int index) =>
        (short)words[index];

    /// <summary>Doc mot word va tra ve gia tri 16-bit khong dau.</summary>
    public static ushort ToUInt16(ReadOnlySpan<ushort> words, int index) =>
        words[index];

    /// <summary>Ghep 2 word thanh int32 (word[0] la 16 bit thap).</summary>
    public static int ToInt32(ReadOnlySpan<ushort> words, int index)
    {
        var value = (uint)words[index] | (uint)words[index + 1] << 16;
        return unchecked((int)value);
    }

    /// <summary>Ghep 2 word thanh uint32 (word[0] la 16 bit thap).</summary>
    public static uint ToUInt32(ReadOnlySpan<ushort> words, int index) =>
        (uint)ToInt32(words, index);

    /// <summary>Ghep 4 word thanh int64 (word[0] la 16 bit thap).</summary>
    public static long ToInt64(ReadOnlySpan<ushort> words, int index)
    {
        ulong value = 0;
        for (var i = 0; i < 4; i++)
            value |= (ulong)words[index + i] << (16 * i);
        return unchecked((long)value);
    }

    /// <summary>Ghep 4 word thanh ulong64 (word[0] la 16 bit thap).</summary>
    public static ulong ToUInt64(ReadOnlySpan<ushort> words, int index) =>
        unchecked((ulong)ToInt64(words, index));

    /// <summary>Ghep 2 word thanh float (IEEE 754).</summary>
    public static float ToSingle(ReadOnlySpan<ushort> words, int index)
    {
        var bits = (uint)words[index] | (uint)words[index + 1] << 16;
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    /// <summary>Ghep 4 word thanh double (IEEE 754).</summary>
    public static double ToDouble(ReadOnlySpan<ushort> words, int index)
    {
        ulong bits = 0;
        for (var i = 0; i < 4; i++)
            bits |= (ulong)words[index + i] << (16 * i);
        return BitConverter.UInt64BitsToDouble(bits);
    }

    // ------------------------------------------------------------------
    // Ghi (gia tri -> word)
    // ------------------------------------------------------------------

    /// <summary>Tach mot gia tri 16-bit co dau thanh 1 word.</summary>
    public static ushort[] FromInt16(short value) => [(ushort)value];

    /// <summary>Tach mot gia tri 16-bit khong dau thanh 1 word.</summary>
    public static ushort[] FromUInt16(ushort value) => [value];

    /// <summary>Tach mot gia tri 32-bit thanh 2 word theo thu tu CDAB.</summary>
    public static ushort[] FromInt32(int value)
    {
        var u = unchecked((uint)value);
        return [(ushort)(u & 0xFFFF), (ushort)(u >> 16)];
    }

    /// <summary>Tach mot gia tri 64-bit thanh 4 word theo thu tu CDAB.</summary>
    public static ushort[] FromInt64(long value)
    {
        var u = unchecked((ulong)value);
        return
        [
            (ushort)(u & 0xFFFF),
            (ushort)((u >> 16) & 0xFFFF),
            (ushort)((u >> 32) & 0xFFFF),
            (ushort)((u >> 48) & 0xFFFF),
        ];
    }

    /// <summary>Tach mot gia tri float thanh 2 word theo thu tu CDAB.</summary>
    public static ushort[] FromSingle(float value) =>
        FromInt32(BitConverter.SingleToInt32Bits(value));

    /// <summary>Tach mot gia tri double thanh 4 word theo thu tu CDAB.</summary>
    public static ushort[] FromDouble(double value) =>
        FromInt64(BitConverter.DoubleToInt64Bits(value));

    // ------------------------------------------------------------------
    // Bit
    // ------------------------------------------------------------------

    /// <summary>Tach bit thu <paramref name="bit"/> trong word (bit 0 la bit thu nhat).</summary>
    public static bool GetBit(ushort word, int bit) =>
        (word & (1 << bit)) != 0;

    /// <summary>Dat bit <paramref name="bit"/> trong word, giu nguyen cac bit khac.</summary>
    public static ushort SetBit(ushort word, int bit, bool value)
    {
        var mask = (ushort)(1 << bit);
        return value ? (ushort)(word | mask) : (ushort)(word & ~mask);
    }
}

using System.Buffers.Binary;
using System.Text;

namespace ClassLibraryMT.Communications.Plc;

/// <summary>
/// Ma hoa / giai ma frame FINS/UDP.
///
/// Header 10 byte:
/// ICF(1) RSV(1) GCT(1) DNA(1) DA1(1) DA2(1) SNA(1) SA1(1) SA2(1) SID(1)
/// </summary>
internal static class FinsCodec
{
    /// <summary>
    /// Tao header FINS cho request.
    /// </summary>
    public static byte[] BuildHeader(
        byte sid, byte remoteNode, byte localNode, bool isResponse = false)
    {
        var header = new byte[Fins.HeaderLength];
        header[0] = isResponse ? Fins.IcfResponse : Fins.IcfRequest;
        header[1] = 0x00;                       // RSV
        header[2] = Fins.Gct;                  // GCT
        header[3] = Fins.Dna;                  // DNA
        header[4] = remoteNode;                // DA1 - node PLC
        header[5] = 0x00;                      // DA2 - unit
        header[6] = Fins.Sna;                  // SNA
        header[7] = localNode;                 // SA1 - node PC
        header[8] = 0x00;                      // SA2 - unit
        header[9] = sid;                       // SID
        return header;
    }

    /// <summary>
    /// Tao lenh doc Memory Area (MRC=0x01 SRC=0x01) cho vung D.
    /// </summary>
    public static byte[] BuildReadCommand(
        byte sid, byte remoteNode, byte localNode, int wordAddress, int wordCount)
    {
        // Than lenh: MRC(1) SRC(1) area(1) dia chi(2) bit(1) so word(2) = 8 byte.
        var frame = new byte[Fins.HeaderLength + Fins.CommandLength];
        var header = BuildHeader(sid, remoteNode, localNode);
        header.CopyTo(frame, 0);

        var p = Fins.HeaderLength;
        frame[p++] = Fins.MrcMemoryArea;   // MRC
        frame[p++] = Fins.SrcRead;         // SRC
        frame[p++] = Fins.AreaD;           // area code (D word)
        WriteUInt16BigEndian(frame.AsSpan(p), (ushort)wordAddress); p += 2;
        frame[p++] = 0x00;                 // bit address (luon 0 cho vung D word)
        WriteUInt16BigEndian(frame.AsSpan(p), (ushort)wordCount); p += 2;

        return frame;
    }

    /// <summary>
    /// Tao lenh ghi Memory Area (MRC=0x01 SRC=0x02) cho vung D.
    /// </summary>
    public static byte[] BuildWriteCommand(
        byte sid, byte remoteNode, byte localNode, int wordAddress, ReadOnlySpan<ushort> words)
    {
        // Than lenh: MRC(1) SRC(1) area(1) dia chi(2) bit(1) so word(2) = 8 byte, + du lieu.
        var frame = new byte[Fins.HeaderLength + Fins.CommandLength + (words.Length * 2)];
        var header = BuildHeader(sid, remoteNode, localNode);
        header.CopyTo(frame, 0);

        var p = Fins.HeaderLength;
        frame[p++] = Fins.MrcMemoryArea;   // MRC
        frame[p++] = Fins.SrcWrite;        // SRC
        frame[p++] = Fins.AreaD;           // area code (D word)
        WriteUInt16BigEndian(frame.AsSpan(p), (ushort)wordAddress); p += 2;
        frame[p++] = 0x00;                 // bit address
        WriteUInt16BigEndian(frame.AsSpan(p), (ushort)words.Length); p += 2;

        foreach (var w in words)
        {
            WriteUInt16BigEndian(frame.AsSpan(p), w);
            p += 2;
        }

        return frame;
    }

    /// <summary>
    /// Kiem tra header response co hop le khong (ICF phai la 0xC0).
    /// </summary>
    public static bool IsValidResponseHeader(ReadOnlySpan<byte> response, out byte sid)
    {
        sid = 0;
        if (response.Length < Fins.HeaderLength) return false;
        if (response[0] != Fins.IcfResponse) return false;

        sid = response[9];
        return true;
    }

    /// <summary>
    /// Doc end code (2 byte big-endian) trong response.
    ///
    /// Cau truc response chuan (giong HslCommunication):
    ///   [0..9] header | [10] MRC | [11] SRC | [12..13] end code | [14..] du lieu
    ///
    /// End code luon nam o offset 12, khong phu thuoc MRC/SRC.
    /// </summary>
    public static bool TryReadEndCode(ReadOnlySpan<byte> response, out ushort endCode)
    {
        endCode = 0;
        var p = Fins.HeaderLength;
        if (response.Length < p + 4) return false;

        endCode = ReadUInt16BigEndian(response.Slice(p + 2, 2));
        return true;
    }

    /// <summary>
    /// Kiem tra MRC/SRC co echo dung lenh da gui khong.
    /// </summary>
    public static bool HasEchoedCommand(ReadOnlySpan<byte> response, byte mrc, byte src)
    {
        if (response.Length < Fins.HeaderLength + 2) return false;
        return response[Fins.HeaderLength] == mrc && response[Fins.HeaderLength + 1] == src;
    }

    /// <summary>
    /// Doc du lieu word (big-endian) nam sau end code trong response lenh doc.
    /// </summary>
    public static bool TryReadData(
        ReadOnlySpan<byte> response, int expectedWords, out ushort[] words)
    {
        words = [];
        var p = Fins.HeaderLength + 4;   // MRC/SRC + end code

        if (response.Length < p) return false;

        var available = response.Length - p;
        var count = Math.Min(expectedWords, available / 2);
        if (count <= 0) return false;

        var result = new ushort[count];
        for (var i = 0; i < count; i++)
            result[i] = ReadUInt16BigEndian(response.Slice(p + (i * 2), 2));

        words = result;
        return true;
    }

    private static void WriteUInt16BigEndian(Span<byte> target, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(target, value);

    private static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt16BigEndian(source);

    /// <summary>
    /// Chuyen doi chuoi ASCII sang word theo quy uoc Omron:
    /// 2 ky tu / word, moi word dao byte (byte thu nhat va byte thu hai doi cho nhau),
    /// duoi 0x00.
    ///
    /// Vi du "ABC" (length 6) -> bytes 41 42 43 00 00 00 -> words 42 41 00 43 00 00
    /// </summary>
    public static ushort[] EncodeStringAsWords(string? value, int length)
    {
        var bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
        var wordCount = (length + 1) / 2;
        var words = new ushort[wordCount];

        for (var i = 0; i < wordCount; i++)
        {
            var first = i * 2 < bytes.Length ? bytes[i * 2] : (byte)0;
            var second = i * 2 + 1 < bytes.Length ? bytes[i * 2 + 1] : (byte)0;
            // Dao byte trong word: byte thu hai len cao, byte thu nhat xuong thap.
            words[i] = (ushort)((second << 8) | first);
        }

        return words;
    }

    /// <summary>
    /// Chuyen doi word sang chuoi ASCII theo quy uoc Omron
    /// (2 ky tu / word, moi word dao byte, dung tai ky tu 0x00).
    /// </summary>
    public static string DecodeStringFromWords(ReadOnlySpan<ushort> words, int length)
    {
        var chars = new char[length];
        var count = 0;

        for (var w = 0; w < words.Length && count < length; w++)
        {
            var high = (byte)(words[w] >> 8);
            var low = (byte)(words[w] & 0xFF);

            // Do lai thu tu: byte thap truoc, byte cao sau.
            if (low != 0 && count < length) chars[count++] = (char)low;
            if (high != 0 && count < length) chars[count++] = (char)high;
        }

        return new string(chars, 0, count);
    }
}

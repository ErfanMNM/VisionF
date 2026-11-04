namespace ClassLibraryMT.Communications.Plc;

/// <summary>Cac phan ghi du lieu cua <see cref="OmronPlc"/>.</summary>
public sealed partial class OmronPlc
{
    // ------------------------------------------------------------------
    // Write - so nguyen 16 bit
    // ------------------------------------------------------------------

    /// <summary>Ghi 1 word (short).</summary>
    public PlcResult WriteInt16(string address, short value) =>
        WriteWords(address, FinsData.FromInt16(value));

    /// <summary>Ghi 1 word (ushort).</summary>
    public PlcResult WriteUInt16(string address, ushort value) =>
        WriteWords(address, FinsData.FromUInt16(value));

    // ------------------------------------------------------------------
    // Write - so nguyen 32 bit
    // ------------------------------------------------------------------

    /// <summary>Ghi 2 word (int).</summary>
    public PlcResult WriteInt32(string address, int value) =>
        WriteWords(address, FinsData.FromInt32(value));

    /// <summary>Ghi 2 word (uint).</summary>
    public PlcResult WriteUInt32(string address, uint value) =>
        WriteWords(address, FinsData.FromInt32(unchecked((int)value)));

    // ------------------------------------------------------------------
    // Write - so nguyen 64 bit
    // ------------------------------------------------------------------

    /// <summary>Ghi 4 word (long).</summary>
    public PlcResult WriteInt64(string address, long value) =>
        WriteWords(address, FinsData.FromInt64(value));

    /// <summary>Ghi 4 word (ulong).</summary>
    public PlcResult WriteUInt64(string address, ulong value) =>
        WriteWords(address, FinsData.FromInt64(unchecked((long)value)));

    // ------------------------------------------------------------------
    // Write - so thuc
    // ------------------------------------------------------------------

    /// <summary>Ghi 2 word (float).</summary>
    public PlcResult WriteFloat(string address, float value) =>
        WriteWords(address, FinsData.FromSingle(value));

    /// <summary>Ghi 4 word (double).</summary>
    public PlcResult WriteDouble(string address, double value) =>
        WriteWords(address, FinsData.FromDouble(value));

    // ------------------------------------------------------------------
    // Write - bool
    // ------------------------------------------------------------------

    /// <summary>
    /// Ghi 1 bit trong 1 word bang read-modify-write. Dia chi dang "D12.05".
    /// </summary>
    public PlcResult WriteBool(string address, bool value)
    {
        if (!FinsAddress.TryParse(address, out var parsed) || !parsed.IsBit)
            return PlcResult.Fail("ARG", $"Dia chi bit khong hop le: '{address}'.", default);

        var read = ReadWordsAt(parsed.WordAddress, 1);
        if (!read.IsSuccess)
            return PlcResult.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var updated = FinsData.SetBit(read.Value![0], parsed.BitAddress!.Value, value);
        return WriteWordsCore(parsed.WordAddress, [updated], read.ProcessTime);
    }

    // ------------------------------------------------------------------
    // Write - chuoi
    // ------------------------------------------------------------------

    /// <summary>
    /// Ghi chuoi ASCII, 2 ky tu / word. Tu pad 0x00 va cat dung do dai.
    /// </summary>
    public PlcResult WriteString(string address, string value, int length)
    {
        if (length <= 0)
            return PlcResult.Fail("ARG", $"Do dai chuoi phai > 0 (nhan {length}).", default);

        var words = FinsCodec.EncodeStringAsWords(value, length);
        return WriteWords(address, words);
    }

    // ------------------------------------------------------------------
    // Write - mang
    // ------------------------------------------------------------------

    /// <summary>Ghi mang (short).</summary>
    public PlcResult WriteInt16Array(string address, short[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var words = new ushort[values.Length];
        for (var i = 0; i < values.Length; i++)
            words[i] = unchecked((ushort)values[i]);
        return WriteWords(address, words);
    }

    /// <summary>Ghi mang (ushort).</summary>
    public PlcResult WriteUInt16Array(string address, ushort[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return WriteWords(address, values);
    }

    /// <summary>Ghi mang (int), moi phan tu 2 word.</summary>
    public PlcResult WriteInt32Array(string address, int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var words = new ushort[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            var pair = FinsData.FromInt32(values[i]);
            words[i * 2] = pair[0];
            words[i * 2 + 1] = pair[1];
        }
        return WriteWords(address, words);
    }

    /// <summary>Ghi mang (uint), moi phan tu 2 word.</summary>
    public PlcResult WriteUInt32Array(string address, uint[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var words = new ushort[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            var pair = FinsData.FromInt32(unchecked((int)values[i]));
            words[i * 2] = pair[0];
            words[i * 2 + 1] = pair[1];
        }
        return WriteWords(address, words);
    }

    /// <summary>Ghi mang (long), moi phan tu 4 word.</summary>
    public PlcResult WriteInt64Array(string address, long[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var words = new ushort[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            var quad = FinsData.FromInt64(values[i]);
            for (var j = 0; j < 4; j++)
                words[i * 4 + j] = quad[j];
        }
        return WriteWords(address, words);
    }

    /// <summary>Ghi mang (ulong), moi phan tu 4 word.</summary>
    public PlcResult WriteUInt64Array(string address, ulong[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var words = new ushort[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            var quad = FinsData.FromInt64(unchecked((long)values[i]));
            for (var j = 0; j < 4; j++)
                words[i * 4 + j] = quad[j];
        }
        return WriteWords(address, words);
    }

    /// <summary>Ghi mang (float), moi phan tu 2 word.</summary>
    public PlcResult WriteFloatArray(string address, float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var words = new ushort[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            var pair = FinsData.FromSingle(values[i]);
            words[i * 2] = pair[0];
            words[i * 2 + 1] = pair[1];
        }
        return WriteWords(address, words);
    }

    /// <summary>Ghi mang (double), moi phan tu 4 word.</summary>
    public PlcResult WriteDoubleArray(string address, double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var words = new ushort[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            var quad = FinsData.FromDouble(values[i]);
            for (var j = 0; j < 4; j++)
                words[i * 4 + j] = quad[j];
        }
        return WriteWords(address, words);
    }

    /// <summary>
    /// Ghi mang bool lien tiep tu dia chi "D100.00" bang read-modify-write.
    /// </summary>
    public PlcResult WriteBoolArray(string address, bool[] values)
    {
        if (!FinsAddress.TryParse(address, out var parsed) || !parsed.IsBit)
            return PlcResult.Fail("ARG", $"Dia chi bit khong hop le: '{address}'.", default);

        ArgumentNullException.ThrowIfNull(values);

        if (values.Length == 0)
            return PlcResult.Fail("ARG", "Mang bool rong.", default);

        var startBit = parsed.BitAddress!.Value;
        var wordCount = (startBit + values.Length + 15) / 16;

        var read = ReadWordsAt(parsed.WordAddress, wordCount);
        if (!read.IsSuccess)
            return PlcResult.Fail(read.ErrorCode, read.Message, read.ProcessTime);

        var words = new ushort[wordCount];
        read.Value!.CopyTo(words, 0);

        for (var i = 0; i < values.Length; i++)
        {
            var absoluteBit = startBit + i;
            var wordIndex = absoluteBit / 16;
            words[wordIndex] = FinsData.SetBit(words[wordIndex], absoluteBit % 16, values[i]);
        }

        return WriteWordsCore(parsed.WordAddress, words, read.ProcessTime);
    }

    /// <summary>
    /// Ghi mang word tho (raw).
    /// </summary>
    public PlcResult WriteWords(string address, short[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var words = new ushort[values.Length];
        for (var i = 0; i < values.Length; i++)
            words[i] = unchecked((ushort)values[i]);
        return WriteWords(address, words);
    }

    /// <summary>
    /// Ghi mang word tho (raw).
    /// </summary>
    public PlcResult WriteWords(string address, ushort[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (!FinsAddress.TryParse(address, out var parsed))
            return PlcResult.Fail("ARG", $"Dia chi khong hop le: '{address}'.", default);

        if (parsed.IsBit)
            return PlcResult.Fail("ARG", $"Dia chi '{address}' la dia chi bit, hay dung WriteBool.", default);

        if (values.Length == 0)
            return PlcResult.Fail("ARG", "Mang word rong.", default);

        if (!parsed.FitsWordCount(values.Length))
            return PlcResult.Fail("ARG",
                $"Ghi {values.Length} word tu '{address}' se tran qua gioi han vung D (32767).", default);

        return WriteWordsCore(parsed.WordAddress, values, default);
    }

    /// <summary>Ghi word (noi bo, da kiem tra pham vi).</summary>
    private PlcResult WriteWordsCore(int wordAddress, ReadOnlySpan<ushort> words, TimeSpan elapsed)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = new List<ushort>(words.Length);
        var offset = 0;
        var current = wordAddress;

        while (offset < words.Length)
        {
            var chunk = Math.Min(words.Length - offset, Fins.MaxWordsPerRequest);
            var result = _transport.Write(_remoteNode, _localNode, current, words.Slice(offset, chunk));

            if (!result.IsSuccess)
                return PlcResult.Fail(result.ErrorCode, result.Message, sw.Elapsed + elapsed);

            offset += chunk;
            current += chunk;
        }

        return PlcResult.Ok("OK", sw.Elapsed + elapsed);
    }

    /// <summary>Dong ket noi UDP.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.Dispose();
    }
}

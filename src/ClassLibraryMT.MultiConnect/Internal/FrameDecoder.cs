using System.Text;

namespace ClassLibraryMT.MultiConnect;

/// <summary>
/// Ghép byte UTF-8 thành các frame text, tách bởi <c>\r</c> hoặc <c>\n</c>
/// (đúng protocol mà <c>CodeReader</c> của camera đang dùng). Có giới hạn độ dài
/// để một thiết bị gửi rác không làm phình bộ nhớ.
/// </summary>
internal sealed class FrameDecoder
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _current = new();
    private readonly int _maxLength;

    public FrameDecoder(int maxLength) => _maxLength = Math.Max(16, maxLength);

    /// <summary>Đưa bytes mới vào và trả về các frame đã hoàn chỉnh.</summary>
    public List<string> Push(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<string>();
        if (bytes.IsEmpty)
        {
            return frames;
        }

        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = _decoder.GetChars(bytes, chars, flush: false);

        for (var i = 0; i < count; i++)
        {
            var ch = chars[i];
            if (ch is '\r' or '\n')
            {
                if (_current.Length > 0)
                {
                    frames.Add(_current.ToString());
                    _current.Clear();
                }
                continue;
            }

            // Bỏ control character (trừ dấu tách dòng).
            if (ch < 0x20)
            {
                continue;
            }

            if (_current.Length < _maxLength)
            {
                _current.Append(ch);
            }
            else
            {
                frames.Add(_current.ToString());
                _current.Clear();
            }
        }

        return frames;
    }

    /// <summary>Trả về phần dư khi đóng kết nối (nếu có).</summary>
    public string? Flush()
    {
        if (_current.Length == 0)
        {
            return null;
        }

        var text = _current.ToString();
        _current.Clear();
        return text;
    }
}

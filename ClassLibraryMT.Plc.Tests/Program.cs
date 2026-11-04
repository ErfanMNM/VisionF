using System.Net;
using System.Net.Sockets;
using ClassLibraryMT.Communications.Plc;

namespace ClassLibraryMT.Plc.Tests;

/// <summary>
/// Test cho client FINS/UDP Omron.
///
/// Phan 1: test voi fake UDP server (khong can PLC that).
/// Phan 2: test voi PLC that neu bien env var PLC_TEST_IP duoc dat.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("=== TEST FINS/UDP CLIENT OMRON ===");
        Console.WriteLine();

        Console.WriteLine("--- 5.4 Parse dia chi ---");
        TestAddressParsing();

        Console.WriteLine();
        Console.WriteLine("--- 5.5/5.6 Frame FINS ---");
        TestFrameFormat();

        Console.WriteLine();
        Console.WriteLine("--- 5.7 Timeout + retry ---");
        TestTimeoutAndRetry();

        Console.WriteLine();
        Console.WriteLine("--- 5.8 End code loi ---");
        TestEndCodes();

        Console.WriteLine();
        Console.WriteLine("--- 5.8 Response co byte thong bao ---");
        TestNotificationBytes();

        Console.WriteLine();
        Console.WriteLine("--- 5.8 SID sai ---");
        TestWrongSid();

        Console.WriteLine();
        Console.WriteLine("--- 5.9/5.10 Doc/ghi moi kieu ---");
        TestReadWriteRoundTrip();

        Console.WriteLine();
        Console.WriteLine("--- 5.11 Chia nho request mang ---");
        TestLargeArraySplit();

        Console.WriteLine();
        Console.WriteLine("--- Loi tham so ---");
        TestArgumentValidation();

        Console.WriteLine();
        Console.WriteLine("--- Test voi PLC that (neu co) ---");
        TestAgainstRealPlc();

        return TestRunner.Summary();
    }

    // ------------------------------------------------------------------
    // 5.4 Parse dia chi
    // ------------------------------------------------------------------
    private static void TestAddressParsing()
    {
        TestRunner.Run("Parse 'D16' doc 1 word", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var r = plc.ReadWords("D16", 1);
            TestRunner.IsTrue(r.IsSuccess);

            var req = server.Requests[0];
            TestRunner.AreEqual(0x00, req[13], "addr byte cao");
            TestRunner.AreEqual(0x10, req[14], "addr byte thap");
        });

        TestRunner.Run("Parse 'DM16' giong 'D16'", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            plc.ReadWords("DM16", 1);
            var req = server.Requests[0];
            TestRunner.AreEqual(0x10, req[14], "addr byte thap");
        });

        TestRunner.Run("Parse 'D0016' giong 'D16'", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            plc.ReadWords("D0016", 1);
            var req = server.Requests[0];
            TestRunner.AreEqual(0x10, req[14], "addr byte thap");
        });

        TestRunner.Run("Parse 'D12.05' doc bit 5", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            server.WordProvider = _ => 0x0020;   // bit 5 = 1
            var r = plc.ReadBool("D12.05");
            TestRunner.IsTrue(r.IsSuccess);
            TestRunner.IsTrue(r.Value, "bit 5 phai la 1");
        });

        TestRunner.Run("Parse dia chi sai tra ARG khong throw", () =>
        {
            using var plc = new OmronPlc("127.0.0.1", 19997, timeoutMs: 200);

            foreach (var bad in new[] { "XYZ", "D", "DM", "", "D12.16", "D-1", "D32768", "D 12" })
            {
                var r = plc.ReadWords(bad, 1);
                TestRunner.IsFalse(r.IsSuccess, $"'{bad}' phai that bai");
                TestRunner.AreEqual("ARG", r.ErrorCode, $"'{bad}' error code");
            }
        });

        TestRunner.Run("Tran vung D bi chan", () =>
        {
            using var plc = new OmronPlc("127.0.0.1", 19997, timeoutMs: 200);

            var r = plc.ReadInt32Array("D32760", 10);   // 20 word -> tran 32779
            TestRunner.IsFalse(r.IsSuccess);
            TestRunner.AreEqual("ARG", r.ErrorCode);
        });
    }

    // ------------------------------------------------------------------
    // 5.5/5.6 Frame FINS
    // ------------------------------------------------------------------
    private static void TestFrameFormat()
    {
        TestRunner.Run("Header FINS 10 byte dung chuan", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, remoteNode: 0, localNode: 1, timeoutMs: 1000);

            plc.ReadWords("D16", 1);
            var req = server.Requests[0];

            TestRunner.AreEqual(18, req.Length, "do dai frame doc");
            TestRunner.AreEqual(0x80, req[0], "ICF");
            TestRunner.AreEqual(0x00, req[1], "RSV");
            TestRunner.AreEqual(0x02, req[2], "GCT");
            TestRunner.AreEqual(0x00, req[3], "DNA");
            TestRunner.AreEqual(0x00, req[4], "DA1");
            TestRunner.AreEqual(0x00, req[5], "DA2");
            TestRunner.AreEqual(0x00, req[6], "SNA");
            TestRunner.AreEqual(0x01, req[7], "SA1");
            TestRunner.AreEqual(0x00, req[8], "SA2");
            TestRunner.AreEqual(0x00, req[9], "SID");
        });

        TestRunner.Run("Lenh doc dung MRC/SRC/area/dia chi", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            plc.ReadWords("D100", 1);
            var req = server.Requests[0];

            TestRunner.AreEqual(0x01, req[10], "MRC");
            TestRunner.AreEqual(0x01, req[11], "SRC");
            TestRunner.AreEqual(0x82, req[12], "area D word");
            TestRunner.AreEqual(0x00, req[13], "dia chi cao");
            TestRunner.AreEqual(0x64, req[14], "dia chi thap (100)");
            TestRunner.AreEqual(0x00, req[15], "bit address");
            TestRunner.AreEqual(0x00, req[16], "so word cao");
            TestRunner.AreEqual(0x01, req[17], "so word thap");
        });

        TestRunner.Run("Lenh ghi dung MRC/SRC + du lieu big-endian", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            plc.WriteUInt16("D100", 0xABCD);
            var req = server.Requests[0];

            TestRunner.AreEqual(0x01, req[10], "MRC");
            TestRunner.AreEqual(0x02, req[11], "SRC");
            TestRunner.AreEqual(0x82, req[12], "area D word");
            TestRunner.AreEqual(0x00, req[13], "dia chi cao");
            TestRunner.AreEqual(0x64, req[14], "dia chi thap (100)");
            TestRunner.AreEqual(0x00, req[15], "bit address");
            TestRunner.AreEqual(0x00, req[16], "so word cao");
            TestRunner.AreEqual(0x01, req[17], "so word thap");
            TestRunner.AreEqual(0xAB, req[18], "data byte cao");
            TestRunner.AreEqual(0xCD, req[19], "data byte thap");
        });

        TestRunner.Run("SID tang dan moi request", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            plc.ReadWords("D16", 1);
            plc.ReadWords("D16", 1);
            plc.ReadWords("D16", 1);

            TestRunner.AreEqual(3, server.Requests.Count);
            TestRunner.AreEqual(0x00, server.Requests[0][9], "SID 1");
            TestRunner.AreEqual(0x01, server.Requests[1][9], "SID 2");
            TestRunner.AreEqual(0x02, server.Requests[2][9], "SID 3");
        });
    }

    // ------------------------------------------------------------------
    // 5.7 Timeout + retry
    // ------------------------------------------------------------------
    private static void TestTimeoutAndRetry()
    {
        TestRunner.Run("Khong co phan hoi -> TIMEOUT", () =>
        {
            // Server im lang (khong phan hoi) de kiem tra timeout that,
            // vi cong dong tren localhost tra ICMP port unreachable ngay lap tuc.
            using var server = new FakePlcServer { Silent = true };
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 200, retries: 0);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = plc.ReadInt32("D16");
            sw.Stop();

            TestRunner.IsFalse(r.IsSuccess);
            TestRunner.AreEqual("TIMEOUT", r.ErrorCode);
            TestRunner.IsTrue(sw.ElapsedMilliseconds >= 150, $"thoi gian cho >= 150ms (thuc te {sw.ElapsedMilliseconds}ms)");
        });

        TestRunner.Run("Retry thu lai dung so lan", () =>
        {
            using var server = new FakePlcServer { Silent = true };
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 200, retries: 2);

            var r = plc.ReadInt32("D16");
            TestRunner.IsFalse(r.IsSuccess);
            TestRunner.AreEqual("TIMEOUT", r.ErrorCode);
            TestRunner.AreEqual(3, server.Requests.Count, "so request = 1 + 2 retry");
        });

        TestRunner.Run("ProcessTime do duoc ca khi loi", () =>
        {
            using var plc = new OmronPlc("127.0.0.1", 19995, timeoutMs: 150, retries: 0);

            var r = plc.ReadInt32("D16");
            TestRunner.IsFalse(r.IsSuccess);
            TestRunner.IsTrue(r.ProcessTime > TimeSpan.Zero, "ProcessTime phai > 0");
        });
    }

    // ------------------------------------------------------------------
    // 5.8 End code
    // ------------------------------------------------------------------
    private static void TestEndCodes()
    {
        foreach (var (code, description) in new (ushort, string)[]
        {
            (0x1101, "sai area"),
            (0x1103, "dia chi ngoai pham vi"),
            (0x0401, "lenh khong ho tro"),
            (0x0001, "service bi huy"),
        })
        {
            var hex = code.ToString("X4");
            TestRunner.Run($"End code {hex} ({description})", () =>
            {
                using var server = new FakePlcServer { EndCode = code };
                using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

                var r = plc.ReadInt32("D16");
                TestRunner.IsFalse(r.IsSuccess);
                TestRunner.AreEqual(hex, r.ErrorCode, "error code");
                TestRunner.IsTrue(r.Message.Length > 0, "phai co thong diep");
            });
        }

        TestRunner.Run("End code loi khi ghi cung tra error code", () =>
        {
            using var server = new FakePlcServer { EndCode = 0x1103 };
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var r = plc.WriteInt32("D16", 1);
            TestRunner.IsFalse(r.IsSuccess);
            TestRunner.AreEqual("1103", r.ErrorCode);
        });
    }

    // ------------------------------------------------------------------
    // Response co byte thong bao
    // ------------------------------------------------------------------
    private static void TestNotificationBytes()
    {
        TestRunner.Run("Response MRC/SRC echo sai bi bao loi BADRESP", () =>
        {
            // End code va du lieu nam o offset co dinh (12..13 / 14..) nhu HslCommunication.
            // Response co byte thong bao (MRC=SRC=0x00) se khong echo dung lenh da gui.
            using var server = new FakePlcServer { UseNotificationBytes = true };
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var r = plc.ReadInt32("D16");
            TestRunner.IsFalse(r.IsSuccess);
            TestRunner.AreEqual("BADRESP", r.ErrorCode);
        });

        TestRunner.Run("Response MRC/SRC echo dung doc duoc du lieu", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            server.WordProvider = i => i == 0 ? (ushort)0x5678 : (ushort)0x1234;
            var r = plc.ReadInt32("D16");
            TestRunner.IsTrue(r.IsSuccess, r.Message);
            TestRunner.AreEqual(0x12345678, r.Value, "int32 CDAB");
        });
    }

    // ------------------------------------------------------------------
    // SID sai
    // ------------------------------------------------------------------
    private static void TestWrongSid()
    {
        TestRunner.Run("Response sai SID bi bo qua", () =>
        {
            using var server = new FakePlcServer { UseWrongSid = true };
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 400, retries: 0);

            var r = plc.ReadInt32("D16");
            TestRunner.IsFalse(r.IsSuccess);
            TestRunner.AreEqual("SID", r.ErrorCode);
        });
    }

    // ------------------------------------------------------------------
    // 5.9/5.10 Read/Write moi kieu
    // ------------------------------------------------------------------
    private static void TestReadWriteRoundTrip()
    {
        TestRunner.Run("Int16 roundtrip", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteInt16("D16", -1234);
            TestRunner.IsTrue(w.IsSuccess);

            server.WordProvider = _ => unchecked((ushort)0xFB2E);   // -1234
            var r = plc.ReadInt16("D16");
            TestRunner.AreEqual((short)-1234, r.Value);
        });

        TestRunner.Run("UInt16 roundtrip", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteUInt16("D16", 0xABCD);
            TestRunner.IsTrue(w.IsSuccess);

            server.WordProvider = _ => 0xABCD;
            var r = plc.ReadUInt16("D16");
            TestRunner.AreEqual((ushort)0xABCD, r.Value);
        });

        TestRunner.Run("Int32 roundtrip (CDAB)", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteInt32("D16", 0x12345678);
            TestRunner.IsTrue(w.IsSuccess);

            // Ghi ra 2 word theo CDAB: word[0] = 16 bit thap = 0x5678, word[1] = 0x1234
            var req = server.Requests[0];
            TestRunner.AreEqual(0x56, req[18], "word[0] byte cao");
            TestRunner.AreEqual(0x78, req[19], "word[0] byte thap");
            TestRunner.AreEqual(0x12, req[20], "word[1] byte cao");
            TestRunner.AreEqual(0x34, req[21], "word[1] byte thap");

            server.WordProvider = i => i == 0 ? (ushort)0x5678 : (ushort)0x1234;
            var r = plc.ReadInt32("D16");
            TestRunner.AreEqual(0x12345678, r.Value);
        });

        TestRunner.Run("UInt32 roundtrip", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteUInt32("D16", 0xAABBCCDD);
            TestRunner.IsTrue(w.IsSuccess);

            server.WordProvider = i => i == 0 ? (ushort)0xCCDD : (ushort)0xAABB;
            var r = plc.ReadUInt32("D16");
            TestRunner.AreEqual(0xAABBCCDDu, r.Value);
        });

        TestRunner.Run("Int64 roundtrip (4 word CDAB)", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteInt64("D16", 0x1122334455667788L);
            TestRunner.IsTrue(w.IsSuccess);

            var req = server.Requests[0];
            TestRunner.AreEqual(0x77, req[18], "word[0] cao");
            TestRunner.AreEqual(0x88, req[19], "word[0] thap");
            TestRunner.AreEqual(0x11, req[24], "word[3] cao");
            TestRunner.AreEqual(0x22, req[25], "word[3] thap");

            server.WordProvider = i => i switch
            {
                0 => (ushort)0x7788,
                1 => (ushort)0x5566,
                2 => (ushort)0x3344,
                _ => (ushort)0x1122,
            };
            var r = plc.ReadInt64("D16");
            TestRunner.AreEqual(0x1122334455667788L, r.Value);
        });

        TestRunner.Run("UInt64 roundtrip", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteUInt64("D16", 0x1122334455667788UL);
            TestRunner.IsTrue(w.IsSuccess);

            server.WordProvider = i => i switch
            {
                0 => (ushort)0x7788,
                1 => (ushort)0x5566,
                2 => (ushort)0x3344,
                _ => (ushort)0x1122,
            };
            var r = plc.ReadUInt64("D16");
            TestRunner.AreEqual(0x1122334455667788UL, r.Value);
        });

        TestRunner.Run("Float roundtrip", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteFloat("D16", 3.5f);
            TestRunner.IsTrue(w.IsSuccess);

            // 3.5f = 0x40600000 -> CDAB: word[0] = 0x0000, word[1] = 0x4060
            server.WordProvider = i => i == 0 ? (ushort)0x0000 : (ushort)0x4060;
            var r = plc.ReadFloat("D16");
            TestRunner.AreClose(3.5f, r.Value, 0.0001f);
        });

        TestRunner.Run("Double roundtrip", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteDouble("D16", 1234.5678);
            TestRunner.IsTrue(w.IsSuccess);

            // 1234.5678 = 0x4093_4A45_6D5C_FAAD -> CDAB word thap truoc
            server.WordProvider = i => i switch
            {
                0 => (ushort)0xFAAD,
                1 => (ushort)0x6D5C,
                2 => (ushort)0x4A45,
                _ => (ushort)0x4093,
            };
            var r = plc.ReadDouble("D16");
            TestRunner.AreClose(1234.5678, r.Value, 0.0001);
        });

        TestRunner.Run("String roundtrip (dao byte trong word)", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteString("D100", "HELLO", 10);
            TestRunner.IsTrue(w.IsSuccess);

            // "HE" -> byte 48 45 -> dao trong word -> 45 48 -> word 0x4548
            var req = server.Requests[0];
            TestRunner.AreEqual(0x45, req[18], "chuoi: byte cao");
            TestRunner.AreEqual(0x48, req[19], "chuoi: byte thap");
            TestRunner.AreEqual(0x4C, req[20], "chuoi 'LL' byte cao");
            TestRunner.AreEqual(0x4C, req[21], "chuoi 'LL' byte thap");

            // "HELLO" 5 ky tu -> 3 word (do dai 10 -> 5 word): word0="HE", word1="LL", word2="O\0"
            // voi dao byte trong word: "HE" -> 0x4548, "LL" -> 0x4C4C, "O" -> 0x004F
            server.WordProvider = i => i switch
            {
                0 => (ushort)0x4548,
                1 => (ushort)0x4C4C,
                2 => (ushort)0x004F,
                _ => (ushort)0x0000,
            };
            var r = plc.ReadString("D100", 10);
            TestRunner.AreEqual("HELLO", r.Value);
        });

        TestRunner.Run("Bool read-modify-write giu nguyen bit khac", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            // 0x0023 = bit 0 (0x01) + bit 1 (0x02) + bit 5 (0x20)
            server.WordProvider = _ => 0x0023;
            var w = plc.WriteBool("D16.05", false);
            TestRunner.IsTrue(w.IsSuccess);

            // Sau khi sua: bit 5 -> 0, bit 0 va bit 1 giu nguyen -> 0x0003
            var req = server.Requests[^1];
            TestRunner.AreEqual(0x00, req[18], "word byte cao");
            TestRunner.AreEqual(0x03, req[19], "word byte thap");
        });

        TestRunner.Run("Bool read-modify-write bat bit", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            server.WordProvider = _ => 0x0000;
            var w = plc.WriteBool("D16.03", true);
            TestRunner.IsTrue(w.IsSuccess);

            // Bit 3 -> 0x08
            var req = server.Requests[^1];
            TestRunner.AreEqual(0x00, req[18], "word byte cao");
            TestRunner.AreEqual(0x08, req[19], "word byte thap");
        });

        TestRunner.Run("Mang Int32 roundtrip", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteInt32Array("D100", [111, 222, 333]);
            TestRunner.IsTrue(w.IsSuccess);

            server.ResponseWordCount = 6;
            server.WordProvider = i => i switch
            {
                0 => (ushort)111,
                1 => (ushort)0,
                2 => (ushort)222,
                3 => (ushort)0,
                4 => (ushort)333,
                _ => (ushort)0,
            };
            var r = plc.ReadInt32Array("D100", 3);
            TestRunner.AreEqual(3, r.Value!.Length);
            TestRunner.AreEqual(111, r.Value[0]);
            TestRunner.AreEqual(222, r.Value[1]);
            TestRunner.AreEqual(333, r.Value[2]);
        });

        TestRunner.Run("Mang Int16 roundtrip", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteInt16Array("D100", [1, -2, 3]);
            TestRunner.IsTrue(w.IsSuccess);

            server.WordProvider = i => i switch
            {
                0 => (ushort)1,
                1 => unchecked((ushort)-2),
                _ => (ushort)3,
            };
            var r = plc.ReadInt16Array("D100", 3);
            TestRunner.AreEqual(3, r.Value!.Length);
            TestRunner.AreEqual((short)1, r.Value[0]);
            TestRunner.AreEqual((short)-2, r.Value[1]);
            TestRunner.AreEqual((short)3, r.Value[2]);
        });

        TestRunner.Run("Mang bool tach/gop bit", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            // Word 0 = 0x0005 -> bit 0 = 1, bit 1 = 0, bit 2 = 1
            server.WordProvider = _ => 0x0005;
            var r = plc.ReadBoolArray("D100.00", 4);
            TestRunner.IsTrue(r.IsSuccess);
            TestRunner.AreEqual(4, r.Value!.Length);
            TestRunner.IsTrue(r.Value[0], "bit 0");
            TestRunner.IsFalse(r.Value[1], "bit 1");
            TestRunner.IsTrue(r.Value[2], "bit 2");
            TestRunner.IsFalse(r.Value[3], "bit 3");
        });

        TestRunner.Run("Mang word tho roundtrip", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 1000);

            var w = plc.WriteWords("D100", [0x1111, 0x2222]);
            TestRunner.IsTrue(w.IsSuccess);

            server.WordProvider = i => i == 0 ? (ushort)0x1111 : (ushort)0x2222;
            var r = plc.ReadWords("D100", 2);
            TestRunner.AreEqual(2, r.Value!.Length);
            TestRunner.AreEqual((ushort)0x1111, r.Value[0]);
            TestRunner.AreEqual((ushort)0x2222, r.Value[1]);
        });
    }

    // ------------------------------------------------------------------
    // 5.11 Chia nho request
    // ------------------------------------------------------------------
    private static void TestLargeArraySplit()
    {
        TestRunner.Run("Mang lon duoc chia nho thanh nhieu request", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 2000);

            var r = plc.ReadInt16Array("D100", 1200);
            TestRunner.IsTrue(r.IsSuccess, r.Message);
            TestRunner.AreEqual(1200, r.Value!.Length, "so phan tu");
            TestRunner.IsTrue(server.Requests.Count >= 3, $"so request = {server.Requests.Count}");

            foreach (var req in server.Requests)
            {
                var wc = (req[16] << 8) | req[17];
                TestRunner.IsTrue(wc <= 500, $"request doc {wc} word");
            }
        });

        TestRunner.Run("Ghi mang lon cung chia nho", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 2000);

            var values = new ushort[1200];
            var w = plc.WriteWords("D100", values);
            TestRunner.IsTrue(w.IsSuccess, w.Message);
            TestRunner.IsTrue(server.Requests.Count >= 3, $"so request = {server.Requests.Count}");

            foreach (var req in server.Requests)
            {
                var wc = (req[16] << 8) | req[17];
                TestRunner.IsTrue(wc <= 500, $"request ghi {wc} word");
            }
        });

        TestRunner.Run("ProcessTime do toan bo mang", () =>
        {
            using var server = new FakePlcServer();
            using var plc = new OmronPlc("127.0.0.1", server.Port, timeoutMs: 2000);

            var r = plc.ReadInt16Array("D100", 1200);
            TestRunner.IsTrue(r.ProcessTime > TimeSpan.Zero);
        });
    }

    // ------------------------------------------------------------------
    // Loi tham so
    // ------------------------------------------------------------------
    private static void TestArgumentValidation()
    {
        TestRunner.Run("length <= 0 tra ARG", () =>
        {
            using var plc = new OmronPlc("127.0.0.1", 19994, timeoutMs: 200);

            TestRunner.AreEqual("ARG", plc.ReadInt32Array("D100", 0).ErrorCode, "length 0");
            TestRunner.AreEqual("ARG", plc.ReadInt32Array("D100", -1).ErrorCode, "length -1");
            TestRunner.AreEqual("ARG", plc.ReadString("D100", 0).ErrorCode, "chuoi length 0");
        });

        TestRunner.Run("Mang rong tra ARG", () =>
        {
            using var plc = new OmronPlc("127.0.0.1", 19993, timeoutMs: 200);

            TestRunner.AreEqual("ARG", plc.WriteInt32Array("D100", []).ErrorCode, "mang int32 rong");
            TestRunner.AreEqual("ARG", plc.WriteWords("D100", Array.Empty<ushort>()).ErrorCode, "mang word rong");
            TestRunner.AreEqual("ARG", plc.WriteBoolArray("D100.00", []).ErrorCode, "mang bool rong");
        });

        TestRunner.Run("Dung dia chi bit cho ham word bi chan", () =>
        {
            using var plc = new OmronPlc("127.0.0.1", 19992, timeoutMs: 200);

            TestRunner.AreEqual("ARG", plc.ReadWords("D12.05", 1).ErrorCode, "ReadWords");
            TestRunner.AreEqual("ARG", plc.WriteWords("D12.05", [1]).ErrorCode, "WriteWords");
        });

        TestRunner.Run("Dia chi bit sai cho ReadBool/WriteBool bi chan", () =>
        {
            using var plc = new OmronPlc("127.0.0.1", 19991, timeoutMs: 200);

            TestRunner.AreEqual("ARG", plc.ReadBool("D12").ErrorCode, "ReadBool khong co bit");
            TestRunner.AreEqual("ARG", plc.WriteBool("D12", true).ErrorCode, "WriteBool khong co bit");
        });
    }

    // ------------------------------------------------------------------
    // PLC that
    // ------------------------------------------------------------------
    private static void TestAgainstRealPlc()
    {
        var ip = Environment.GetEnvironmentVariable("PLC_TEST_IP");
        if (string.IsNullOrWhiteSpace(ip))
        {
            Console.WriteLine("  (bo qua - dat bien moi truong PLC_TEST_IP de chay, vd: PLC_TEST_IP=127.0.0.1)");
            return;
        }

        var port = int.TryParse(Environment.GetEnvironmentVariable("PLC_TEST_PORT"), out var p) ? p : 9600;
        var remoteNode = byte.TryParse(Environment.GetEnvironmentVariable("PLC_TEST_NODE"), out var n) ? n : (byte)0;

        using var plc = new OmronPlc(ip, port, remoteNode: remoteNode, timeoutMs: 2000, retries: 1);

        TestRunner.Run($"PLC that {ip}:{port} doc duoc", () =>
        {
            var r = plc.ReadInt32("D16");
            TestRunner.IsTrue(r.IsSuccess, $"code={r.ErrorCode} msg={r.Message}");
        });

        TestRunner.Run($"PLC that {ip}:{port} ghi va doc lai", () =>
        {
            // Ghi vao vung test, doc lai, khoi phuc gia tri goc.
            var original = plc.ReadInt32("D900");
            TestRunner.IsTrue(original.IsSuccess, "doc gia tri goc");

            var w = plc.WriteInt32("D900", 123456);
            TestRunner.IsTrue(w.IsSuccess, $"ghi: code={w.ErrorCode}");

            var back = plc.ReadInt32("D900");
            TestRunner.AreEqual(123456, back.Value, "gia tri doc lai");

            var restore = plc.WriteInt32("D900", original.Value);
            TestRunner.IsTrue(restore.IsSuccess, "khoi phuc");
        });
    }
}

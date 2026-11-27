using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Data.Sqlite;

internal static class PoContractChecks
{
    private const string Gtin = "09506000134352";
    private const string Valid = "(01)09506000134352(21)Keep,Case";
    private const string Other = "(01)00012345678905(21)Other";

    public static async Task RunAsync(string root, Action<string, bool, string> check)
    {
        using var fixture = new ServerFixture(Path.Combine(root, "contract"), "contract-test-key");
        var raw = new[] { Valid, " invalid raw\r\n", Other, Valid };
        var mixed = await SendAsync(fixture, "orderNo=MIXED&orderQty=4&gtin=" + Gtin, raw);
        check("Partial receive báo đủ từng loại raw", mixed.Status == HttpStatusCode.OK
            && mixed.Result.AcceptedCount == 1 && mixed.Result.Accepted == 1 && mixed.Result.PlannedCount == 4
            && mixed.Result.InvalidCodes.Count == 2 && mixed.Result.DuplicateCodes.SequenceEqual([Valid])
            && mixed.Result.InvalidCodes[0].Code == raw[1] && mixed.Result.InvalidCodes[0].Reason == "invalidStructure"
            && mixed.Result.InvalidCodes[1].Code == Other && mixed.Result.InvalidCodes[1].Reason == "gtinMismatch", mixed.Body);
        var reloaded = new PoStore(fixture.Store.DbRoot);
        var saved = (await reloaded.ListLocalOrdersAsync()).Single();
        var codes = await reloaded.GetCodesAsync(saved.OrderNo, saved.CreatedUtc);
        check("Reload giữ planned khác actual và nguyên GTIN metadata", saved.OrderQty == 4 && saved.TotalCodes == 1
            && saved.Gtin == Gtin && codes.Single().CodeContent == Valid, "");
        var replay = await SendAsync(fixture, "poName=MIXED&orderQty=4&gtin=" + Gtin, raw);
        check("Replay không tăng actual hoặc thay planned", replay.Result.AcceptedCount == 0
            && (await fixture.OrderAsync("MIXED")).OrderQty == 4 && (await fixture.CodesAsync("MIXED")).Count == 1, replay.Body);
        var appendOther = await SendAsync(fixture, "orderNo=MIXED&orderQty=1&gtin=00012345678905", [Other]);
        check("GTIN khác bị chặn sau reload", appendOther.Status == HttpStatusCode.UnprocessableEntity
            && (await fixture.CodesAsync("MIXED")).Count == 1 && (await fixture.OrderAsync("MIXED")).OrderQty == 4, appendOther.Body);
        var shortGtin = await SendAsync(fixture, "orderNo=PADDED&orderQty=1&gtin=4006381333931", ["(01)04006381333931(21)Raw"]);
        var shortGtinOrder = await fixture.OrderAsync("PADDED");
        var shortGtinMeta = await fixture.Store.GetMetaAsync(shortGtinOrder.OrderNo, shortGtinOrder.CreatedUtc);
        check("Nhận mã AI01 GTIN14 nhưng lưu nguyên metadata GTIN13", shortGtin.Status == HttpStatusCode.OK
            && shortGtinOrder.Gtin == "4006381333931" && shortGtinMeta[PoStore.MetaGtin] == "4006381333931", shortGtin.Body);
        var duplicate = await SendAsync(fixture, "orderNo=PADDED&orderQty=1&gtin=04006381333931", ["(01)04006381333931(21)Raw"]);
        check("Replay GTIN tương đương trả duplicate và giữ nguyên GTIN13 cũ", duplicate.Status == HttpStatusCode.OK
            && duplicate.Result.AcceptedCount == 0 && duplicate.Result.IgnoredDuplicates == 1
            && (await fixture.OrderAsync("PADDED")).Gtin == "4006381333931", duplicate.Body);
        await SendAsync(fixture, "orderNo=DUP-PLANNED&orderQty=1&gtin=" + Gtin + "&productName=Keep", [Valid]);
        var beforeTarget = await fixture.OrderAsync("DUP-PLANNED");
        // Trigger chứng minh metadata-only merge không đụng các dòng mã/lịch sử scan.
        await using (var db = new SqliteConnection("Data Source=" + fixture.Store.GetDbPath(beforeTarget.OrderNo, beforeTarget.CreatedUtc)))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER no_code_update BEFORE UPDATE ON PoCodes BEGIN SELECT RAISE(ABORT, 'code update forbidden'); END; "
                + "CREATE TRIGGER no_code_insert BEFORE INSERT ON PoCodes BEGIN SELECT RAISE(ABORT, 'code insert forbidden'); END;";
            await command.ExecuteNonQueryAsync();
        }
        var raisedTarget = await SendAsync(fixture, "orderNo=DUP-PLANNED&orderQty=4&gtin=" + Gtin + "&productName=Ignore", [Valid, Valid, Valid, Valid]);
        var target = await fixture.OrderAsync("DUP-PLANNED");
        check("Toàn duplicate vẫn persist planned max, không đụng mã/scan", raisedTarget.Status == HttpStatusCode.OK
            && raisedTarget.Result.AcceptedCount == 0 && raisedTarget.Result.PlannedCount == 4
            && raisedTarget.Result.IgnoredDuplicates == 4 && target.OrderQty == 4 && target.TotalCodes == 1
            && target.Additional["productName"] == "Keep" && (await fixture.CodesAsync("DUP-PLANNED")).Single().CodeContent == Valid, raisedTarget.Body);
        await SendAsync(fixture, "orderNo=DUP-PLANNED&orderQty=1&gtin=" + Gtin, [Valid]);
        check("Replay duplicate nhỏ hơn không hạ planned", (await fixture.OrderAsync("DUP-PLANNED")).OrderQty == 4, "");

        string[] badMetadata = [
            "orderNo=BAD&orderQty=1", "orderNo=BAD&orderQty=1&gtin=09506000134353",
            "orderNo=BAD&orderQty=1&gtin=09506000134352,00012345678905",
            "orderNo=BAD&poName=ALIAS&orderQty=1&gtin=" + Gtin,
            "orderNo=BAD&orderQty=1&gtin=" + Gtin + "&gtin=" + Gtin,
            "orderNo=BAD&orderQty=2&gtin=" + Gtin,
        ];
        foreach (var query in badMetadata)
        {
            var result = await SendAsync(fixture, query, [Valid]);
            check("Metadata/count lỗi không tạo DB " + query.Split('&')[1], !result.Result.Success
                && result.Status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
                && fixture.Store.FindExistingDbPath("BAD") is null, result.Body);
        }
        var allInvalid = await SendAsync(fixture, "orderNo=ALL-INVALID&orderQty=2&gtin=" + Gtin, ["not-gs1", Other]);
        check("Toàn mã sai trả 422 nguyên tử", allInvalid.Status == HttpStatusCode.UnprocessableEntity
            && allInvalid.Result.InvalidCodes.Count == 2 && fixture.Store.FindExistingDbPath("ALL-INVALID") is null, allInvalid.Body);

        await fixture.Store.EnsurePoDbAsync(new ProductionOrder { OrderNo = "LEGACY-BAD", OrderQty = 1, CreatedUtc = DateTime.UtcNow },
            [new PoCode { CodeContent = "opaque-legacy" }]);
        var legacy = await SendAsync(fixture, "orderNo=LEGACY-BAD&orderQty=1&gtin=" + Gtin, [Valid]);
        check("Legacy không xác minh được GTIN bị chặn append", legacy.Status == HttpStatusCode.UnprocessableEntity
            && (await fixture.CodesAsync("LEGACY-BAD")).Single().CodeContent == "opaque-legacy", legacy.Body);
        await fixture.Store.EnsurePoDbAsync(new ProductionOrder { OrderNo = "LEGACY-MIXED", Gtin = Gtin, OrderQty = 2, CreatedUtc = DateTime.UtcNow },
            [new PoCode { CodeContent = Valid }, new PoCode { CodeContent = Other }]);
        var mixedLegacy = await SendAsync(fixture, "orderNo=LEGACY-MIXED&orderQty=1&gtin=" + Gtin, ["(01)09506000134352(21)New"]);
        check("Metadata GTIN không che mã cũ trộn GTIN", mixedLegacy.Status == HttpStatusCode.UnprocessableEntity
            && (await fixture.CodesAsync("LEGACY-MIXED")).Count == 2, mixedLegacy.Body);

        using var anonymous = new HttpClient { BaseAddress = fixture.Http.BaseAddress };
        foreach (var path in new[] { "po-demo/", "po-demo/validation.js" })
        {
            using var response = await anonymous.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            var resourceName = "ClassLibraryMT.PoServer.PoDemo." + (path.EndsWith(".js", StringComparison.Ordinal) ? "validation.js" : "index.html");
            using var expectedStream = typeof(PoHttpServer).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new Exception("Thiếu embedded resource " + resourceName);
            using var expectedReader = new StreamReader(expectedStream);
            var expected = await expectedReader.ReadToEndAsync();
            check("Static loopback không cần key " + path, response.StatusCode == HttpStatusCode.OK
                && body.Length > 100 && body == expected && response.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff", "");
        }
        using (var denied = await anonymous.GetAsync("api/health"))
            check("Static bypass không lan sang API", denied.StatusCode == HttpStatusCode.Unauthorized, "");
        using (var wrong = new HttpRequestMessage(HttpMethod.Get, "api/health"))
        {
            wrong.Headers.Add("x-api-key", "wrong-test-key");
            using var response = await anonymous.SendAsync(wrong);
            check("Sai key vẫn bị 401", response.StatusCode == HttpStatusCode.Unauthorized, "");
        }
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Head })
        {
            using var request = new HttpRequestMessage(method, "po-demo/");
            using var response = await fixture.Http.SendAsync(request);
            check("Static không nhận method " + method, response.StatusCode == HttpStatusCode.NotFound, "");
        }
        foreach (var path in new[] { "/po-demo", "/PO-DEMO/", "/po-demo/index.html", "/po-demo/../appsettings.json",
                     "/po-demo/%2e%2e/appsettings.json", "/po-demo/%2fvalidation.js", "/po-demo/%5cvalidation.js", "/po-demo/validation.js/" })
        {
            var rawResponse = await RawGetAsync(fixture.Http.BaseAddress!, path);
            check("Static không mở alias/traversal " + path, !rawResponse.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "");
        }
        // Listener rộng vẫn yêu cầu key cho static, kể cả request đến từ loopback.
        using var wide = new ServerFixture(Path.Combine(root, "wide-static"), "wide-test-key", bindAddress: "+");
        using var wideAnonymous = new HttpClient { BaseAddress = wide.Http.BaseAddress };
        using (var response = await wideAnonymous.GetAsync("po-demo/"))
            check("Listener wildcard không bypass static key", response.StatusCode == HttpStatusCode.Unauthorized, "");
        using (var response = await wide.Http.GetAsync("po-demo/"))
            check("Listener wildcard phục vụ static khi đúng key", response.StatusCode == HttpStatusCode.OK, "");
        wide.Options.BindAddress = "127.0.0.1";
        using (var response = await wideAnonymous.GetAsync("po-demo/"))
            check("Đổi cấu hình không mở bypass cho listener wildcard đã bind", response.StatusCode == HttpStatusCode.Unauthorized, "");
        fixture.Options.BindAddress = "+";
        using (var response = await anonymous.GetAsync("po-demo/"))
            check("Static auth dựa listener loopback thực đã bind", response.StatusCode == HttpStatusCode.OK, "");
    }

    private static Task<(HttpStatusCode Status, PoPushResponse Result, string Body)> SendAsync(ServerFixture fixture, string query, string[] codes) =>
        fixture.SendAsync("api/po?" + query, new StringContent(JsonSerializer.Serialize(codes.Select(code => new { CodeContent = code })), Encoding.UTF8, "application/json"));

    private static async Task<string> RawGetAsync(Uri baseAddress, string rawPath)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, baseAddress.Port);
        await using var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes($"GET {rawPath} HTTP/1.1\r\nHost: 127.0.0.1:{baseAddress.Port}\r\nx-api-key: contract-test-key\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}

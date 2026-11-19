using System.Buffers.Binary;
using System.IO;
using System.Text;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Telegram;
using GxbrielZapret.ViewModels;
using Xunit;

namespace GxbrielZapret.Tests;

public class TelegramEngineTests
{
    [Fact]
    public void AesCtr_EncryptDecrypt_MatchesOriginalPlaintext()
    {
        byte[] key = TgProxyProto.RandomBytes(32);
        byte[] iv = TgProxyProto.RandomBytes(16);

        byte[] original = Encoding.UTF8.GetBytes("GXBRIEL_ZAPRET native Telegram MTProto-over-WSS stream test data with length 1234567890!");

        using var enc = new AesCtr(key, iv);
        using var dec = new AesCtr(key, iv);

        byte[] cipher = enc.Update(original);
        byte[] decrypted = dec.Update(cipher);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void AesCtr_StreamingChunks_MaintainsKeystreamAcrossCalls()
    {
        byte[] key = TgProxyProto.RandomBytes(32);
        byte[] iv = TgProxyProto.RandomBytes(16);

        byte[] original = TgProxyProto.RandomBytes(1024);

        using var enc = new AesCtr(key, iv);
        using var dec = new AesCtr(key, iv);

        // Encrypt in chunks of 7, 13, 29, 64 bytes
        var cipherChunks = new List<byte>();
        int pos = 0;
        int[] chunkSizes = [7, 13, 29, 64, 128, 256, 527];

        foreach (int sz in chunkSizes)
        {
            if (pos >= original.Length) break;
            int take = Math.Min(sz, original.Length - pos);
            byte[] chunk = original[pos..(pos + take)];
            cipherChunks.AddRange(enc.Update(chunk));
            pos += take;
        }

        // Decrypt in different chunk sizes
        var plainChunks = new List<byte>();
        byte[] fullCipher = cipherChunks.ToArray();
        pos = 0;
        int[] decChunkSizes = [16, 32, 100, 200, 676];

        foreach (int sz in decChunkSizes)
        {
            if (pos >= fullCipher.Length) break;
            int take = Math.Min(sz, fullCipher.Length - pos);
            byte[] chunk = fullCipher[pos..(pos + take)];
            plainChunks.AddRange(dec.Update(chunk));
            pos += take;
        }

        Assert.Equal(original, plainChunks.ToArray());
    }

    [Fact]
    public void TgProxyProto_Handshake_DecodesSuccessfully()
    {
        byte[] secret = TgProxyProto.RandomBytes(16);
        byte[] protoTag = TgProxyProto.ProtoTagSecure;
        int expectedDc = 2;

        byte[] clientInit = TgProxyProto.GenerateClientInit(protoTag, expectedDc, secret);
        Assert.Equal(64, clientInit.Length);

        var decoded = TgProxyProto.TryHandshake(clientInit, secret);
        Assert.NotNull(decoded);

        var (dc, isMedia, parsedTag, prekeyIv) = decoded.Value;
        Assert.Equal(expectedDc, dc);
        Assert.False(isMedia);
        Assert.Equal(protoTag, parsedTag);
        Assert.Equal(48, prekeyIv.Length);
    }

    [Fact]
    public void TgProxyProto_TestDc_CorrectlyIdentified()
    {
        Assert.True(TgProxyProto.IsTestDc(10001));
        Assert.True(TgProxyProto.IsTestDc(10002));
        Assert.True(TgProxyProto.IsTestDc(10003));
        Assert.False(TgProxyProto.IsTestDc(1));
        Assert.False(TgProxyProto.IsTestDc(2));
        Assert.False(TgProxyProto.IsTestDc(4));
    }

    [Fact]
    public void TgProxyProto_NormalizeDc_MapsAliases()
    {
        Assert.Equal(2, TgProxyProto.NormalizeDc(203));
        Assert.Equal(1, TgProxyProto.NormalizeDc(1));
        Assert.Equal(2, TgProxyProto.NormalizeDc(2));
        Assert.Equal(4, TgProxyProto.NormalizeDc(4));
        Assert.Equal(2, TgProxyProto.NormalizeDc(99)); // Unknown falls back to DC2
    }

    [Fact]
    public void CfProxyBalancer_RotatesAndCoolsDownBadDomains()
    {
        var balancer = new CfProxyBalancer();
        var domains = balancer.DomainsForDc(2, isMedia: false).Take(5).ToList();
        Assert.NotEmpty(domains);

        string badDomain = domains.First();
        balancer.MarkBad(2, badDomain, cooldownMs: 60_000, isMedia: false);

        Assert.True(balancer.IsCooling(2, badDomain, isMedia: false));

        var freshDomains = balancer.DomainsForDc(2, isMedia: false).Take(5).ToList();
        Assert.DoesNotContain(badDomain, freshDomains);
    }

    [Fact]
    public void CfProxyBalancer_SeparatesChatAndMediaLanes()
    {
        var balancer = new CfProxyBalancer();
        string sampleDomain = "test.domain";

        balancer.MarkBad(2, sampleDomain, cooldownMs: 60_000, isMedia: true);

        // Media is cooling, Chat is NOT
        Assert.True(balancer.IsCooling(2, sampleDomain, isMedia: true));
        Assert.False(balancer.IsCooling(2, sampleDomain, isMedia: false));
    }

    [Fact]
    public void MsgSplitter_IntermediateFraming_SplitsPacketsCorrectly()
    {
        byte[] protoTag = TgProxyProto.ProtoTagIntermediate;
        byte[] relayInit = TgProxyProto.GenerateRelayInit(protoTag, 2);

        var splitter = new MsgSplitter(relayInit, TgProxyProto.ProtoIntermediateInt);

        // Create packet with intermediate framing: [len LE: 4] + payload
        byte[] payload1 = Encoding.ASCII.GetBytes("MessagePartOne12345");
        byte[] framed1 = new byte[4 + payload1.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed1, payload1.Length);
        Buffer.BlockCopy(payload1, 0, framed1, 4, payload1.Length);

        // Encrypt with relay cipher
        using var enc = new AesCtr(
            relayInit[TgProxyProto.SkipLen..(TgProxyProto.SkipLen + TgProxyProto.PrekeyLen)],
            relayInit[(TgProxyProto.SkipLen + TgProxyProto.PrekeyLen)..(TgProxyProto.SkipLen + TgProxyProto.PrekeyLen + TgProxyProto.IvLen)]);
        enc.Update(new byte[64]); // Fast-forward past relayInit

        byte[] encFramed1 = enc.Update(framed1);

        var parts = splitter.Split(encFramed1);
        Assert.Single(parts);
        Assert.Equal(encFramed1.Length, parts[0].Length);
    }

    [Fact]
    public void TelegramProxyService_Lifecycle_StartsAndStopsGracefully()
    {
        using var svc = new TelegramProxyService();
        svc.Configure(18443, null); // Use non-standard test port

        Assert.False(svc.IsRunning);

        bool started = svc.Start();
        if (started)
        {
            Assert.True(svc.IsRunning);
            Assert.StartsWith("tg://proxy?server=127.0.0.1", svc.ProxyLink);

            svc.Stop();
            Assert.False(svc.IsRunning);
        }
    }

    [Fact]
    public void RouteManager_ChatAndMediaCandidates_FollowsLanePreferences()
    {
        var mgr = new RouteManager();

        var chatCandidates = mgr.GetCandidates(2, isMedia: false).Take(5).ToList();
        Assert.NotEmpty(chatCandidates);
        Assert.True(chatCandidates.First().IsDirect); // Chat prefers Direct first

        var mediaCandidates = mgr.GetCandidates(2, isMedia: true).Take(5).ToList();
        Assert.NotEmpty(mediaCandidates);
        Assert.False(mediaCandidates.First().IsDirect); // Media prefers CF Fronts first
    }

    [Fact]
    public void RouteManager_Http429Backoff_IncreasesCooldown()
    {
        var mgr = new RouteManager();
        string domain = mgr.AllBaseDomains[0];

        var route = mgr.GetOrCreateRoute(domain);
        Assert.Equal(RouteState.Healthy, route.State);

        // 1st 429
        mgr.RecordFailure(domain, "Http429", 429);
        Assert.True(route.IsCooling);
        Assert.Equal(RouteState.Cooldown, route.State);
        Assert.Equal(1, route.Http429Count);

        // 2nd 429
        mgr.RecordFailure(domain, "Http429", 429);
        Assert.Equal(2, route.Http429Count);
    }

    [Fact]
    public void RouteManager_Success_RecoversHealthScoreAndClearsCooldown()
    {
        var mgr = new RouteManager();
        string domain = mgr.AllBaseDomains[0];

        mgr.RecordFailure(domain, "ConnectFail");
        var route = mgr.GetOrCreateRoute(domain);
        Assert.True(route.IsCooling);

        mgr.RecordSuccess(domain, latencyMs: 80, ttfbMs: 150, bytesIn: 5000, bytesOut: 1000);
        Assert.False(route.IsCooling);
        Assert.Equal(1, route.ConsecutiveSuccesses);
        Assert.Equal(0, route.ConsecutiveFailures);
        Assert.Equal(RouteState.Healthy, route.State);
    }

    [Fact]
    public void StickyPort_GxRestart_ReusesSavedPortWithoutChanges()
    {
        string secretHex = "0123456789abcdef0123456789abcdef";
        int testPort = 19443;

        // Session 1: initial launch
        using (var svc1 = new TelegramProxyService())
        {
            svc1.Configure(testPort, secretHex);
            bool ok = svc1.Start();
            if (ok)
            {
                Assert.Equal(testPort, svc1.Port);
                Assert.False(svc1.IsReconfigureRequired);
                Assert.Equal($"tg://proxy?server=127.0.0.1&port={testPort}&secret=dd{secretHex}", svc1.ProxyLink);
                svc1.Stop();
            }
        }

        // Session 2: GX restarts and reuses saved port
        using (var svc2 = new TelegramProxyService())
        {
            svc2.Configure(testPort, secretHex);
            bool ok = svc2.Start();
            if (ok)
            {
                Assert.Equal(testPort, svc2.Port);
                Assert.False(svc2.IsReconfigureRequired);
                Assert.Equal($"tg://proxy?server=127.0.0.1&port={testPort}&secret=dd{secretHex}", svc2.ProxyLink);
                svc2.Stop();
            }
        }
    }

    [Fact]
    public void StickyPort_ConflictOccurs_FallsBackToFreePort_SetsReconfigureRequired_GeneratesNewLink()
    {
        string secretHex = "aabbccddeeff00112233445566778899";
        int initialPort = 19450;

        // 1. Occupy initialPort with another listener (simulating another process occupying the port)
        var dummyListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, initialPort);
        dummyListener.Start();

        int newSavedPort;
        try
        {
            // 2. Start Telegram Engine on the occupied port
            using var svc = new TelegramProxyService();
            svc.Configure(initialPort, secretHex);
            bool ok = svc.Start();
            Assert.True(ok);

            // 3. Verify it fell back to a different port and marked ReconfigureRequired
            Assert.NotEqual(initialPort, svc.Port);
            Assert.True(svc.IsReconfigureRequired);
            Assert.Contains($"port={svc.Port}", svc.ProxyLink);
            Assert.Contains($"secret=dd{secretHex}", svc.ProxyLink);

            newSavedPort = svc.Port;
            svc.Stop();
        }
        finally
        {
            // 4. Free the original port
            dummyListener.Stop();
        }

        // 5. Simulate GX restart: GX loads the NEW saved port (newSavedPort)
        // Verify GX does NOT jump back to initialPort (19450), retaining sticky port 
        using (var svcRestarted = new TelegramProxyService())
        {
            svcRestarted.Configure(newSavedPort, secretHex);
            bool ok = svcRestarted.Start();
            Assert.True(ok);

            Assert.Equal(newSavedPort, svcRestarted.Port);
            Assert.False(svcRestarted.IsReconfigureRequired); // No reconfigure needed now since it bound saved port
            Assert.Equal($"tg://proxy?server=127.0.0.1&port={newSavedPort}&secret=dd{secretHex}", svcRestarted.ProxyLink);

            svcRestarted.Stop();
        }
    }

    [Fact]
    public void StickyPort_SecretPersistence_KeepsConstantSecretAcrossRestarts()
    {
        string constantSecret = "11223344556677889900aabbccddeeff";
        using var svc = new TelegramProxyService();
        svc.Configure(19460, constantSecret);

        Assert.Equal(constantSecret, svc.SecretHex);
        Assert.StartsWith($"tg://proxy?server=127.0.0.1&port=19460&secret=dd{constantSecret}", svc.ProxyLink);
    }

    [Fact]
    public async Task TelegramProxyService_ClientHandshake_RejectsInvalidSecret()
    {
        using var svc = new TelegramProxyService();
        int testPort = 19470;
        string validSecret = "0123456789abcdef0123456789abcdef";
        string wrongSecret = "fedcba9876543210fedcba9876543210";
        svc.Configure(testPort, validSecret);
        Assert.True(svc.Start());

        try
        {
            using var cli = new System.Net.Sockets.TcpClient();
            await cli.ConnectAsync("127.0.0.1", testPort);
            var stream = cli.GetStream();

            // Send handshake with wrong secret
            byte[] init = TgProxyProto.GenerateClientInit(TgProxyProto.ProtoTagSecure, 2, Convert.FromHexString(wrongSecret));
            await stream.WriteAsync(init);
            await stream.FlushAsync();

            // Proxy should close stream because secret did not match
            var buf = new byte[64];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int read = await stream.ReadAsync(buf, cts.Token);
            Assert.Equal(0, read); // Disconnected by server
        }
        finally
        {
            svc.Stop();
        }
    }

    [Fact]
    public async Task TelegramProxyService_BridgeSelfTest_ExecutesWithoutExceptions()
    {
        using var svc = new TelegramProxyService();
        var logs = new List<string>();
        svc.LogLine += line => logs.Add(line);
        int testPort = 19480;
        svc.Configure(testPort, "0123456789abcdef0123456789abcdef");
        Assert.True(svc.Start());

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            string verdict = await svc.BridgeSelfTestAsync(2, cts.Token);
            Assert.NotEmpty(verdict);
            Assert.NotEmpty(logs);
        }
        finally
        {
            svc.Stop();
        }
    }

    [Fact]
    public void TelegramProxy_ToggleAndStop_SynchronizesDesiredStateAndSettings()
    {
        var settingsSvc = new SettingsService();
        settingsSvc.Settings.DesiredTelegramEnabled = false;
        settingsSvc.Settings.TelegramActual = "STOPPED";
        settingsSvc.Save();

        var vm = new MainViewModel();
        try
        {
            // Initial state: not running
            Assert.False(vm.IsTelegramProxyRunning);
            Assert.False(vm.Settings.DesiredTelegramEnabled);
            Assert.Equal("STOPPED", vm.Settings.TelegramActual);

            // 1. Toggle ON
            vm.ToggleTelegramProxy();
            Assert.True(vm.Settings.DesiredTelegramEnabled);
            if (vm.IsTelegramProxyRunning)
            {
                Assert.Equal("RUNNING", vm.Settings.TelegramActual);

                // 2. Toggle OFF
                vm.ToggleTelegramProxy();
                Assert.False(vm.IsTelegramProxyRunning);
                Assert.False(vm.Settings.DesiredTelegramEnabled);
                Assert.Equal("STOPPED", vm.Settings.TelegramActual);

                // 3. Setter ON
                vm.IsTelegramProxyEnabled = true;
                Assert.True(vm.IsTelegramProxyRunning);
                Assert.True(vm.Settings.DesiredTelegramEnabled);
                Assert.Equal("RUNNING", vm.Settings.TelegramActual);

                // 4. Setter OFF
                vm.IsTelegramProxyEnabled = false;
                Assert.False(vm.IsTelegramProxyRunning);
                Assert.False(vm.Settings.DesiredTelegramEnabled);
                Assert.Equal("STOPPED", vm.Settings.TelegramActual);
            }
        }
        finally
        {
            vm.StopTelegramProxy();
        }
    }

    [Fact]
    public void TelegramProxyLatency_WhenNoSuccessfulRoutes_ReturnsDashInsteadOfFakePing()
    {
        var vm = new MainViewModel();
        try
        {
            // When stopped
            Assert.Equal("—", vm.TelegramProxyLatencyText);

            // Start proxy
            vm.StartTelegramProxy(autoOpenClient: false);
            if (vm.IsTelegramProxyRunning)
            {
                // No route has had a successful probe yet
                Assert.Equal("—", vm.TelegramProxyLatencyText);
                Assert.DoesNotContain("75", vm.TelegramProxyLatencyText);
            }
        }
        finally
        {
            vm.StopTelegramProxy();
        }
    }

    [Fact]
    public async Task TgWebSocket_InvalidTlsCertificate_RejectsWithTlsCertStage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var res = await TgWebSocket.ConnectAsync("1.1.1.1", "invalid.telegram.untrusted.local", TimeSpan.FromSeconds(3), ct: cts.Token);

        Assert.False(res.Ok);
        Assert.True(res.Stage == WsStage.TlsCert || res.Stage == WsStage.Tls || res.Stage == WsStage.Tcp);
    }

    [Fact]
    public async Task TgWebSocket_Valid101WithCorrectAccept_PassesHandshake()
    {
        string wsKey = "dGhlIHNhbXBsZSBub25jZQ==";
        string accept = TgWebSocket.ComputeSecWebSocketAccept(wsKey);
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", accept);

        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(response));
        var (status, ok, error) = await TgWebSocket.ReadUpgradeResponseAsync(ms, wsKey, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(101, status);
        Assert.Null(error);
    }

    [Fact]
    public async Task TgWebSocket_InvalidAccept_FailsHandshake()
    {
        string wsKey = "dGhlIHNhbXBsZSBub25jZQ==";
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: invalidAcceptKey==\r\n\r\n";

        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(response));
        var (status, ok, error) = await TgWebSocket.ReadUpgradeResponseAsync(ms, wsKey, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(101, status);
        Assert.Contains("Sec-WebSocket-Accept", error);
    }

    [Fact]
    public async Task TgWebSocket_MissingUpgradeHeaders_FailsHandshake()
    {
        string wsKey = "dGhlIHNhbXBsZSBub25jZQ==";
        string accept = TgWebSocket.ComputeSecWebSocketAccept(wsKey);
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(response));
        var (status, ok, error) = await TgWebSocket.ReadUpgradeResponseAsync(ms, wsKey, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(101, status);
        Assert.Contains("Upgrade", error);
    }

    [Fact]
    public async Task TgWebSocket_TruncatedEof_FailsHandshake()
    {
        string wsKey = "dGhlIHNhbXBsZSBub25jZQ==";
        string truncated = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n";

        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(truncated));
        var (status, ok, error) = await TgWebSocket.ReadUpgradeResponseAsync(ms, wsKey, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(0, status);
        Assert.NotNull(error);
    }

    [Fact]
    public void TelegramDiagnosticsReport_FormatsSafeDiagnosticTextWithoutSensitiveData()
    {
        var report = new TelegramDiagnosticsReport
        {
            OverallOk = false,
            Summary = "1 node available, 2 blocked"
        };
        report.Steps.Add(new TelegramProbeStep
        {
            TargetName = "Direct DC2",
            Dc = 2,
            Lane = "chat",
            DnsSource = "Direct IP",
            AttemptedEndpoint = "149.154.167.220:443",
            DnsOk = true,
            TcpOk = false,
            OutcomeSummary = "TCP Timeout/Blocked"
        });
        report.Steps.Add(new TelegramProbeStep
        {
            TargetName = "CF Front (example.co.uk)",
            Dc = 2,
            Lane = "chat",
            DnsSource = "DoH",
            AttemptedEndpoint = "188.114.96.1:443 (kws2.example.co.uk)",
            DnsOk = true,
            TcpOk = true,
            TlsOk = true,
            WsOk = true,
            WsStatus = 101,
            MtProtoResPqOk = true,
            LatencyMs = 85,
            OutcomeSummary = "OK"
        });

        string safeText = report.ToSafeDiagnosticText();
        Assert.Contains("=== GXBRIEL_ZAPRET Telegram Diagnostics", safeText);
        Assert.Contains("Direct DC2", safeText);
        Assert.Contains("TCP FAIL", safeText);
        Assert.Contains("CF Front (example.co.uk)", safeText);
        Assert.Contains("85 ms", safeText);
        Assert.DoesNotContain("secret", safeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", safeText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TelegramDiagnostics_DetailedDiagnosticCheck_ProducesStructuredReport()
    {
        using var svc = new TelegramProxyService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var report = await svc.DetailedDiagnosticCheckAsync(cts.Token);

        Assert.NotNull(report);
        Assert.NotEmpty(report.Summary);
        Assert.NotEmpty(report.Steps);
        foreach (var s in report.Steps)
        {
            Assert.NotEmpty(s.TargetName);
            Assert.NotEmpty(s.AttemptedEndpoint);
        }
    }
}

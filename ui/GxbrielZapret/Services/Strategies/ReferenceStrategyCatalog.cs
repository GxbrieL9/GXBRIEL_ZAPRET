using GxbrielZapret.Models;

namespace GxbrielZapret.Services.Strategies;

/// <summary>
/// A typed, immutable model of a reference baseline strategy in GXBRIEL_ZAPRET.
/// Immutable baseline of 39 strategies, plus separately identified upstream candidates:
/// - 9 built-ins (GX-B01 .. GX-B09)
/// - 30 catalog probe candidates (GX-C01 .. GX-C30)
/// </summary>
public sealed record ReferenceStrategy(
    string Id,
    string Name,
    string Tagline,
    string Description,
    string Provenance,
    string TargetServices,
    IReadOnlyList<string> CompatibleLists,
    IReadOnlyList<string> Args,
    bool IsRecommended = false
)
{
    public string Fingerprint => CanonicalProfile.ComputeFingerprint(Args);

    public Preset ToPreset()
    {
        return new Preset
        {
            Name = Name,
            Tagline = Tagline,
            Description = Description,
            ReferenceId = Id,
            Provenance = Provenance,
            TargetServices = TargetServices,
            CompatibleLists = new List<string>(CompatibleLists),
            Args = new List<string>(Args),
            UsesHostlist = true,
            IsBuiltIn = true,
            IsRecommended = IsRecommended,
            Status = "BROKEN",
            Reason = "unproven",
            Fingerprint = Fingerprint
        };
    }
}

/// <summary>
/// Central immutable reference catalog, preserving the original 39 baseline strategies:
/// GX-B01 .. GX-B09 (9 built-in UI strategies)
/// GX-C01 .. GX-C30 (30 catalog probe candidates)
///
/// This catalog serves as the single source of truth for the reference baseline.
/// It cannot be mutated, edited, or deleted by user CRUD, importers, or auto-generators.
/// </summary>
public static class ReferenceStrategyCatalog
{
    // =========================================================================
    // Shared argument builders (guaranteeing exact token & syntax parity)
    // =========================================================================

    private static readonly string[] RecDiscordTls = { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4" };
    private static readonly string[] RecYoutubeTls =
    {
        "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_seq=-10000:repeats=6",
        "--lua-desync=multidisorder:pos=1,midsld",
    };

    private static readonly string[] QuicFake6 = { "--lua-desync=fake:blob=fake_default_quic:repeats=6" };
    private static readonly string[] QuicFake11 = { "--lua-desync=fake:blob=fake_default_quic:repeats=11" };

    private static List<string> BuildCandidateArgs(string[] tls, string[] quic) => new List<string>
    {
        "--wf-tcp-out=80,443-65535",
        "--wf-udp-out=443-65535",
        "--ctrack-disable=0",
        "--ipcache-lifetime=8400",
        "--ipcache-hostname=1",
        "--lua-init=fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')",
        "--blob=quic_google:@{FILES}\\fake\\quic_initial_www_google_com.bin",
        "--blob=tls_google:@{FILES}\\fake\\tls_clienthello_www_google_com.bin",
        "--blob=tls_vk:@{FILES}\\fake\\tls_clienthello_vk_com.bin",
        "--blob=tls_sber:@{FILES}\\fake\\tls_clienthello_sberbank_ru.bin",
        "--blob=tls_gos:@{FILES}\\fake\\tls_clienthello_gosuslugi_ru.bin",
        "--wf-raw-part=@{WF}\\windivert_part.stun.txt",
        "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
        "--filter-tcp=80", "--filter-l7=http", "--out-range=-d10", "--payload=http_req",
          "--lua-desync=fake:blob=fake_default_http:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
          "--lua-desync=fakedsplit:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
        "--new",
        "--filter-tcp=443-65535", "--filter-l7=tls", "--out-range=-d10", "--payload=tls_client_hello",
    }
    .Concat(tls)
    .Concat(new[]
    {
        "--new",
        "--filter-udp=443-65535", "--filter-l7=quic", "--payload=quic_initial",
    })
    .Concat(quic)
    .Concat(new[]
    {
        "--new",
        "--filter-udp=19294-19344,50000-65535", "--filter-l7=discord,stun",
          "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=2",
    })
    .ToList();

    // =========================================================================
    // 9 Built-In Reference Strategies (GX-B01 .. GX-B09)
    // =========================================================================

    private static readonly ReferenceStrategy[] ShippedBuiltIns =
    {
        // GX-B01: Recommended Combo
        new(
            Id: "GX-B01",
            Name: "Комбо (рекомендуемый)",
            Tagline: "Универсальная — начните с неё",
            Description: "Лучшее под каждый сервис в одной команде, маршрутизация по SNI. Discord → hostfakesplit (быстрый логин + медиа), YouTube/видеопоток → hostfakesplit md5×6, остальное → hostfakesplit. Голос: STUN + RTP-фикс. Не нужно переключаться между пресетами.",
            Provenance: "ZAPRET2UI_ADAPTED",
            TargetServices: "Discord web/login/media/voice, YouTube, fallback TLS",
            CompatibleLists: new[] { "discord", "youtube", "exclude" },
            Args: PresetService.BuildComboArgs(RecDiscordTls, RecYoutubeTls, RecDiscordTls),
            IsRecommended: true
        ),

        // GX-B02: Targeted Domestic Combo (VK)
        new(
            Id: "GX-B02",
            Name: "Комбо — отечественный (VK, целевой)",
            Tagline: "Маскировка под VK",
            Description: "Целенаправленно под РФ: фейки для Discord маскируются под vk.com (ТСПУ не роняет отечественный трафик). Как и рекомендуемый — hostfakesplit по маркеру SNI, поэтому пускает и в логин, и к серверам (гейтвею), а не только на страницу входа. Discord/остальное → hostfakesplit vk.com, YouTube → fake(google)+multidisorder (vk-фейк для googlevideo не годится — там нужен обычный рабочий профиль). Голос: STUN + RTP-фикс. Первый выбор, если google-варианты «зелёные, но не открывают» Discord.",
            Provenance: "GX_CUSTOM",
            TargetServices: "Discord, YouTube, fallback TLS",
            CompatibleLists: new[] { "discord", "youtube", "exclude" },
            Args: PresetService.BuildComboArgs(
                new[] { "--lua-desync=hostfakesplit:host=vk.com:tcp_ts=-1000:tcp_md5:repeats=4" },
                RecYoutubeTls,
                new[] { "--lua-desync=hostfakesplit:host=vk.com:tcp_ts=-1000:tcp_md5:repeats=4" }
            )
        ),

        // GX-B03: Flowseal ALT10 Combo
        new(
            Id: "GX-B03",
            Name: "Комбо — Flowseal ALT10 (двойной fake + ts)",
            Tagline: "Когда не работают голос и медиа",
            Description: "Перевод Flowseal general (ALT10) на nfqws2: без разрезки — двойной fake-пакет (google + vk-ClientHello) с ts-fooling, голос через отечественный QUIC-блоб (vk). Часто пробивает и гейтвей, и медиа, и голос там, где сплит-стратегии дают только логин. Первый выбор при «залогинился, но не подключает / войс молчит».",
            Provenance: "FLOWSEAL_ADAPTED",
            TargetServices: "Discord gateway/media/voice, YouTube",
            CompatibleLists: new[] { "discord", "youtube", "exclude" },
            Args: PresetService.BuildComboArgs(
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                        "--lua-desync=fake:blob=tls_vk:tcp_ts=-1000:repeats=6" },
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:ip_id=zero:repeats=6" },
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                        "--lua-desync=fake:blob=tls_vk:tcp_ts=-1000:repeats=6" },
                voiceDesync: new[] { "--lua-desync=fake:blob=quic_vk:repeats=6" }
            )
        ),

        // GX-B04: Flowseal ALT11 Combo
        new(
            Id: "GX-B04",
            Name: "Комбо — Flowseal ALT11 (fake+ts → seqovl)",
            Tagline: "Фейк + разрезка внахлёст",
            Description: "Перевод Flowseal general (ALT11) на nfqws2: fake-пакет с ts-fooling + multisplit с большим seqovl (реальный google-ClientHello как паттерн), голос через отечественный QUIC-блоб (vk). Если ALT10 не зашёл — пробуйте этот.",
            Provenance: "FLOWSEAL_ADAPTED",
            TargetServices: "Discord gateway/media/voice, YouTube",
            CompatibleLists: new[] { "discord", "youtube", "exclude" },
            Args: PresetService.BuildComboArgs(
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                        "--lua-desync=multisplit:pos=2:seqovl=681:seqovl_pattern=tls_google:optional" },
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                        "--lua-desync=multisplit:pos=2:seqovl=681:seqovl_pattern=tls_google:ip_id=zero:optional" },
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                        "--lua-desync=multisplit:pos=2:seqovl=664:seqovl_pattern=tls_google:optional" },
                voiceDesync: new[] { "--lua-desync=fake:blob=quic_vk:repeats=6" }
            )
        ),

        // GX-B05: Flowseal multisplit seqovl Combo
        new(
            Id: "GX-B05",
            Name: "Комбо — Flowseal (multisplit seqovl)",
            Tagline: "Разрезка запроса",
            Description: "Актуальная стратегия Flowseal general, переведённая на nfqws2: multisplit с большим seqovl и реальным ClientHello google как паттерном, раздельно по SNI. Голос: STUN + RTP-фикс.",
            Provenance: "FLOWSEAL_ADAPTED",
            TargetServices: "Discord, YouTube, fallback TLS, voice",
            CompatibleLists: new[] { "discord", "youtube", "exclude" },
            Args: PresetService.BuildComboArgs(
                new[] { "--lua-desync=multisplit:pos=2:seqovl=681:seqovl_pattern=tls_google:optional" },
                new[] { "--lua-desync=multisplit:pos=2:seqovl=681:seqovl_pattern=tls_google:ip_id=zero:optional" },
                new[] { "--lua-desync=multisplit:pos=2:seqovl=568:seqovl_pattern=tls_google:optional" }
            )
        ),

        // GX-B06: Flowseal ALT Combo
        new(
            Id: "GX-B06",
            Name: "Комбо — Flowseal ALT (fake+fakedsplit)",
            Tagline: "Фейк + ложная разрезка",
            Description: "Альтернатива Flowseal ALT: fake с tcp_ts + fakedsplit (fooling только на фейках), раздельно по SNI. Голос: STUN + RTP-фикс. Пробуйте, если multisplit-вариант не пробивает.",
            Provenance: "FLOWSEAL_ADAPTED",
            TargetServices: "Discord, YouTube, fallback TLS, voice",
            CompatibleLists: new[] { "discord", "youtube", "exclude" },
            Args: PresetService.BuildComboArgs(
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                        "--lua-desync=fakedsplit:tcp_ts=-1000" },
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                        "--lua-desync=fakedsplit:tcp_ts=-1000" },
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                        "--lua-desync=fakedsplit:tcp_ts=-1000" }
            )
        ),

        // GX-B07: Window Size (wssize) Combo
        new(
            Id: "GX-B07",
            Name: "Комбо — окно (wssize)",
            Tagline: "Дробит ответ сервера",
            Description: "Для упрямого блока входа: разрезка ClientHello (реальный паттерн) + wssize заставляет сервер дробить ответ. YouTube → fake+multidisorder. Голос: STUN + RTP-фикс. Медленнее, но иногда пробивает там, где остальное нет.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "Stubborn Discord login blocks, YouTube, voice",
            CompatibleLists: new[] { "discord", "youtube", "exclude" },
            Args: PresetService.BuildComboArgs(
                new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional",
                        "--lua-desync=wssize:wsize=1:scale=6" },
                new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_seq=-10000:repeats=6",
                        "--lua-desync=multidisorder:pos=1,midsld" },
                new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional",
                        "--lua-desync=wssize:wsize=1:scale=6" }
            )
        ),

        // GX-B08: Standalone Discord Voice
        new(
            Id: "GX-B08",
            Name: "Discord — голос (QUIC-фейк)",
            Tagline: "Только голос Discord",
            Description: "Только починка голоса (без разбивки TLS по SNI). discord+stun на голосовых портах фейкуются QUIC-блобом google — сервер отбрасывает его как мусор, поэтому SSRC не портится и нет NO_ROUTE. Для случая «подключается, но никого не слышно / пинг 5000 / Connection timed out». Без ограничения ttl (рабочий путь Flowseal alt10).",
            Provenance: "GX_CUSTOM",
            TargetServices: "Discord voice/media only",
            CompatibleLists: new[] { "discord" },
            Args: new[]
            {
                "--wf-tcp-out=80,443-65535",
                "--wf-udp-out=443-65535",
                "--ctrack-disable=0",
                "--ipcache-lifetime=8400",
                "--ipcache-hostname=1",
                "--wf-raw-part=@{WF}\\windivert_part.stun.txt",
                "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
                "--blob=quic_google:@{FILES}\\fake\\quic_initial_www_google_com.bin",
                "--blob=tls_google:@{FILES}\\fake\\tls_clienthello_www_google_com.bin",
                "--filter-tcp=443-65535", "--filter-l7=tls", "{HOSTLIST:discord}", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional",
                "--new",
                "--filter-udp=19294-19344,50000-65535", "--filter-l7=discord,stun",
                  "--lua-desync=fake:blob=quic_google:repeats=6",
            }
        ),

        // GX-B09: Adaptive Circular Discord
        new(
            Id: "GX-B09",
            Name: "Discord — адаптивный (circular, эксперим.)",
            Tagline: "Подбирает способ на ходу",
            Description: "Самоподстраивающийся пресет ТОЛЬКО под Discord: circular сам переключает стратегию (hostfakesplit → двойной fake → seqovl), когда ловит RST/ретрансмиссию от DPI. Ровно для «вход проходит, но не подключает / войс молчит». Крутит варианты на лету (без перезапуска движка) и оседает на первом рабочем — дайте ему несколько секунд после старта. YouTube не трогает; запускайте, когда упрямится именно Discord.",
            Provenance: "GX_CUSTOM",
            TargetServices: "Discord web/login/gateway/media/voice only",
            CompatibleLists: new[] { "discord" },
            Args: new[]
            {
                "{WF_TCP}",
                "{WF_UDP}",
                "--ctrack-disable=0",
                "--ipcache-lifetime=8400",
                "--ipcache-hostname=1",
                "--blob=tls_google:@{FILES}\\fake\\tls_clienthello_www_google_com.bin",
                "--blob=tls_vk:@{FILES}\\fake\\tls_clienthello_vk_com.bin",
                "--blob=quic_google:@{FILES}\\fake\\quic_initial_www_google_com.bin",
                "--blob=quic_vk:@{FILES}\\fake\\quic_initial_vk_com.bin",
                "--wf-raw-part=@{WF}\\windivert_part.stun.txt",
                "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
                "--filter-tcp=443-65535", "--filter-l7=tls", "{HOSTLIST:discord}",
                  "--in-range=-s5556", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=circular:fails=2:time=300",
                  "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4:strategy=1",
                  "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6:strategy=2",
                  "--lua-desync=fake:blob=tls_vk:tcp_ts=-1000:repeats=6:strategy=2",
                  "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6:strategy=3",
                  "--lua-desync=multisplit:pos=1,midsld:seqovl=681:seqovl_pattern=tls_google:strategy=3:optional",
                "--new",
                "--filter-udp=443-65535", "--filter-l7=quic", "{HOSTLIST:discord}", "--payload=quic_initial",
                  "--lua-desync=fake:blob=quic_google:repeats=11",
                "--new",
                "--filter-udp=19294-19344,50000-65535", "--filter-l7=discord,stun",
                  "--lua-desync=fake:blob=quic_vk:repeats=6",
            }
        )
    };

    // =========================================================================
    // 30 Catalog Probe Candidates (GX-C01 .. GX-C30)
    // =========================================================================

    private static readonly ReferenceStrategy[] ShippedCatalogCandidates =
    {
        // GX-C01: Proven Discord Pipeline
        new(
            Id: "GX-C01",
            Name: "DC: send→syndata→fake ack→multisplit seqovl",
            Tagline: "Discord: syndata + multisplit",
            Description: "Проверенный пайплайн Discord (youtubediscord Example 2): send + syndata в SYN, fake с tcp_ack=-66000 и multisplit с seqovl=700 (google ClientHello). Самый надёжный кандидат для входа в Discord.",
            Provenance: "YOUTUBEDISCORD_ADAPTED",
            TargetServices: "Discord TLS/login/media plus QUIC/voice combo wrapper",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=send:repeats=2",
                        "--lua-desync=syndata:blob=tls_google",
                        "--lua-desync=fake:blob=tls_google:tcp_ack=-66000:tcp_ts_up:tls_mod=rnd:repeats=2",
                        "--lua-desync=multisplit:pos=2,midsld-2:seqovl=700:seqovl_pattern=tls_google:optional" },
                QuicFake11
            )
        ),

        // GX-C02: Legacy Discord Recipe
        new(
            Id: "GX-C02",
            Name: "DC legacy: fake ts → multidisorder sld seqovl",
            Tagline: "Discord legacy: multidisorder",
            Description: "Классический рецепт Discord: fake с tcp_ts=-1000 + multidisorder_legacy с перекрытием на позиции seqovl=sld+1.",
            Provenance: "YOUTUBEDISCORD_ADAPTED",
            TargetServices: "Discord TLS/login plus combo wrapper",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_google:repeats=6:tcp_ts=-1000",
                        "--lua-desync=multidisorder_legacy:pos=1,sld+2,midsld:seqovl=sld+1:seqovl_pattern=tls_google:optional" },
                QuicFake6
            )
        ),

        // GX-C03: Discord Media High TCP
        new(
            Id: "GX-C03",
            Name: "DC media: syndata → fake badack → multisplit seqovl",
            Tagline: "Discord медиа: syndata + badack",
            Description: "Рецепт под discord.media (высокие порты TCP): syndata в SYN, fake bad-ack с низким autottl, затем чистый multisplit seqovl=336.",
            Provenance: "YOUTUBEDISCORD_ADAPTED",
            TargetServices: "Discord media/high TCP plus combo wrapper",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=send:repeats=2",
                        "--lua-desync=syndata:blob=tls_google:ip_autottl=-2,3-20",
                        "--lua-desync=fake:blob=tls_google:tcp_ack=-66000:tcp_ts_up:ip_autottl=-2,3-20:repeats=6",
                        "--lua-desync=multisplit:pos=1,midsld:seqovl=336:seqovl_pattern=tls_google:optional" },
                QuicFake11
            )
        ),

        // GX-C04: Window Size (wssize 1:6)
        new(
            Id: "GX-C04",
            Name: "Окно wssize 1:6",
            Tagline: "Дробит окно TCP (1:6)",
            Description: "Zero-phase рычаг zapret2 против упрямых блокировок входа: принуждает сервер дробить ответ через оконное масштабирование wssize wsize=1 scale=6.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "Stubborn TLS blocks plus combo wrapper",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=wssize:wsize=1:scale=6" },
                QuicFake6
            )
        ),

        // GX-C05: Window Size + seqovl tls_google
        new(
            Id: "GX-C05",
            Name: "Окно wssize 1:6 + seqovl tls_google",
            Tagline: "Окно TCP + seqovl",
            Description: "Комбинация multisplit seqovl=1 по маркеру ClientHello google и сужения окна сервера wssize 1:6.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "TLS blocks plus combo wrapper",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional",
                        "--lua-desync=wssize:wsize=1:scale=6" },
                QuicFake6
            )
        ),

        // GX-C06: Window Size + fake ts md5
        new(
            Id: "GX-C06",
            Name: "Окно wssize 1:6 + fake ts md5",
            Tagline: "Окно TCP + fake ts md5",
            Description: "Фейковый google ClientHello с tcp_md5 и tcp_ts=-1000 перед оконным сплитом wssize 1:6.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "TLS blocks plus combo wrapper",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_ts=-1000:repeats=6",
                        "--lua-desync=wssize:wsize=1:scale=6" },
                QuicFake11
            )
        ),

        // GX-C07: Flowseal ALT11/12 (seqovl 681)
        new(
            Id: "GX-C07",
            Name: "ALT11/12: fake ts → multisplit seqovl 681",
            Tagline: "Flowseal ALT11: seqovl 681",
            Description: "Паттерн Flowseal ALT11/12: прайм fake c tcp_ts=-1000 + multisplit с байтовым seqovl=681 реального google ClientHello.",
            Provenance: "FLOWSEAL_ADAPTED",
            TargetServices: "Discord/YouTube combo probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=8",
                        "--lua-desync=multisplit:pos=2:seqovl=681:seqovl_pattern=tls_google:optional" },
                QuicFake11
            )
        ),

        // GX-C08: Flowseal ALT (seqovl 664)
        new(
            Id: "GX-C08",
            Name: "ALT: fake ts → multisplit seqovl 664",
            Tagline: "Flowseal ALT: seqovl 664",
            Description: "Альтернативный паттерн Flowseal: прайм fake c tcp_ts=-1000 + multisplit с байтовым seqovl=664.",
            Provenance: "FLOWSEAL_ADAPTED",
            TargetServices: "Discord/YouTube combo probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=8",
                        "--lua-desync=multisplit:pos=2:seqovl=664:seqovl_pattern=tls_google:optional" },
                QuicFake11
            )
        ),

        // GX-C09: YouTube TCP Multisplit Seqovl
        new(
            Id: "GX-C09",
            Name: "YT: multisplit 2,midsld-2 seqovl tls",
            Tagline: "YouTube: multisplit seqovl",
            Description: "Универсальная разрезка ClientHello вокруг SNI с нахлёстом реального паттерна google. Лучший общий профиль для YouTube.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "YouTube TLS plus combo wrapper",
            CompatibleLists: new[] { "youtube", "discord" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional" },
                QuicFake6
            )
        ),

        // GX-C10: YouTube googlevideo Multidisorder
        new(
            Id: "GX-C10",
            Name: "YT googlevideo: multidisorder multi-pos",
            Tagline: "googlevideo: multidisorder",
            Description: "Многоточечный multidisorder в обратном порядке сегментов по маркерам host/sld/sni/endhost. Обходит DPI со сборкой по sequence.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "YouTube/googlevideo plus combo wrapper",
            CompatibleLists: new[] { "youtube", "discord" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2" },
                QuicFake6
            )
        ),

        // GX-C11: YouTube Current Community Recipe (dupsid)
        new(
            Id: "GX-C11",
            Name: "YT актуальный: fake dupsid → multidisorder SNI",
            Tagline: "YouTube актуальный: dupsid + SNI",
            Description: "Актуальный рецепт 2026: фейк с dupsid и rnd перед глубоким SNI-маркерным multidisorder.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "YouTube plus combo wrapper",
            CompatibleLists: new[] { "youtube", "discord" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_google:tls_mod=rnd,dupsid:repeats=6",
                        "--lua-desync=multidisorder:pos=1,sniext+1,host+1,midsld-2,midsld,endhost-1" },
                QuicFake11
            )
        ),

        // GX-C12: FAKE md5sig + Multidisorder
        new(
            Id: "GX-C12",
            Name: "FAKE md5sig + multidisorder midsld",
            Tagline: "Фейк md5sig + multidisorder",
            Description: "Фейк с TCP MD5 сигнатурой и отрицательным смещением sequence (-10000) + multidisorder midsld.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "Broad TLS combo probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_seq=-10000:repeats=6",
                        "--lua-desync=multidisorder:pos=1,midsld" },
                QuicFake11
            )
        ),

        // GX-C13: FAKE autottl + Multisplit
        new(
            Id: "GX-C13",
            Name: "FAKE autottl + multisplit midsld",
            Tagline: "Фейк autottl + multisplit",
            Description: "Фейк с автоматическим подбором TTL (autottl) и MD5 + multisplit midsld.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "Broad TLS combo probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5:repeats=6",
                        "--lua-desync=multisplit:pos=midsld" },
                QuicFake6
            )
        ),

        // GX-C14: fakedsplit md5sig
        new(
            Id: "GX-C14",
            Name: "fakedsplit md5sig midsld",
            Tagline: "fakedsplit: fooling на фейках",
            Description: "Ложная разрезка fakedsplit:pos=midsld с защитной MD5-сигнатурой только на фейковых сегментах.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "Broad TLS combo probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fakedsplit:pos=midsld:tcp_md5" },
                QuicFake11
            )
        ),

        // GX-C15: hostfakesplit google (ts + md5)
        new(
            Id: "GX-C15",
            Name: "hostfakesplit ts md5",
            Tagline: "hostfakesplit google (ts+md5)",
            Description: "Разрезка по маркеру SNI с инъекцией google-хоста, tcp_ts=-1000 и tcp_md5. Адаптируется к размеру handshake гейтвея Discord.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "Discord gateway-safe TLS plus combo wrapper",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4" },
                QuicFake6
            )
        ),

        // GX-C16: hostfakesplit google md5 ×6
        new(
            Id: "GX-C16",
            Name: "hostfakesplit md5 ×6",
            Tagline: "hostfakesplit google md5 ×6",
            Description: "Инъекция google-хоста с 6 повторами и TCP MD5 сигнатурой.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "Discord/TLS plus combo wrapper",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_md5:repeats=6" },
                QuicFake6
            )
        ),

        // GX-C17: hostfakesplit + wssize
        new(
            Id: "GX-C17",
            Name: "hostfakesplit + wssize",
            Tagline: "hostfakesplit + окно wssize",
            Description: "Сочетание адаптивного hostfakesplit google и оконного сплита wssize 1:6 для труднодоступных логин-серверов.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "Discord login/TLS plus combo wrapper",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4",
                        "--lua-desync=wssize:wsize=1:scale=6" },
                QuicFake6
            )
        ),

        // GX-C18: hostfakesplit MS-host
        new(
            Id: "GX-C18",
            Name: "hostfakesplit MS-host",
            Tagline: "hostfakesplit microsoft.com",
            Description: "Инъекция хоста www.microsoft.com с tcp_ts и tcp_md5 вместо google.",
            Provenance: "GX_CUSTOM",
            TargetServices: "Domestic/alternate fake-host TLS probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=hostfakesplit:host=www.microsoft.com:tcp_ts=-1000:tcp_md5:repeats=4" },
                QuicFake6
            )
        ),

        // GX-C19: hostfakesplit VK-host ts md5
        new(
            Id: "GX-C19",
            Name: "hostfakesplit VK-host ts md5",
            Tagline: "hostfakesplit vk.com (ts+md5)",
            Description: "Отечественная маскировка: инъекция vk.com с tcp_ts=-1000 и tcp_md5. ТСПУ пропускает домашний трафик без блокировки.",
            Provenance: "GX_CUSTOM",
            TargetServices: "Russian ISP domestic fake-host probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=hostfakesplit:host=vk.com:tcp_ts=-1000:tcp_md5:repeats=4" },
                QuicFake6
            )
        ),

        // GX-C20: hostfakesplit ozon.ru md5 ×6
        new(
            Id: "GX-C20",
            Name: "hostfakesplit ozon.ru md5 ×6",
            Tagline: "hostfakesplit ozon.ru md5 ×6",
            Description: "Отечественная маскировка под ozon.ru с 6 повторами и tcp_md5 (актуальный тренд sonicdpi).",
            Provenance: "GX_CUSTOM",
            TargetServices: "Russian ISP domestic fake-host probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=hostfakesplit:host=ozon.ru:tcp_md5:repeats=6" },
                QuicFake6
            )
        ),

        // GX-C21: DC отечественный VK-CH md5 → multidisorder
        new(
            Id: "GX-C21",
            Name: "DC отеч.: fake VK-CH md5 → multidisorder",
            Tagline: "Фейк VK ClientHello + multidisorder",
            Description: "Реальный ClientHello vk.com с md5 и autottl, за которым следует multidisorder pos=1,midsld.",
            Provenance: "GX_CUSTOM",
            TargetServices: "Discord domestic ClientHello probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_vk:tcp_md5:ip_autottl=-2,3-20:repeats=6",
                        "--lua-desync=multidisorder:pos=1,midsld" },
                QuicFake11
            )
        ),

        // GX-C22: DC отечественный Sber-CH md5 → multidisorder
        new(
            Id: "GX-C22",
            Name: "DC отеч.: fake Sber-CH md5 → multidisorder",
            Tagline: "Фейк Сбер ClientHello + multidisorder",
            Description: "Реальный ClientHello sberbank.ru с md5 и autottl перед multidisorder.",
            Provenance: "GX_CUSTOM",
            TargetServices: "Discord domestic ClientHello probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_sber:tcp_md5:ip_autottl=-2,3-20:repeats=6",
                        "--lua-desync=multidisorder:pos=1,midsld" },
                QuicFake11
            )
        ),

        // GX-C23: hostfakesplit Sber-host ts md5
        new(
            Id: "GX-C23",
            Name: "hostfakesplit Sber-host ts md5",
            Tagline: "hostfakesplit sberbank.ru",
            Description: "Инъекция sberbank.ru с tcp_ts=-1000 и tcp_md5 (белый список ТСПУ).",
            Provenance: "GX_CUSTOM",
            TargetServices: "Russian ISP domestic fake-host probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=hostfakesplit:host=sberbank.ru:tcp_ts=-1000:tcp_md5:repeats=4" },
                QuicFake6
            )
        ),

        // GX-C24: hostfakesplit Gos-host ts md5
        new(
            Id: "GX-C24",
            Name: "hostfakesplit Gos-host ts md5",
            Tagline: "hostfakesplit gosuslugi.ru",
            Description: "Инъекция gosuslugi.ru с tcp_ts=-1000 и tcp_md5 (белый список ТСПУ).",
            Provenance: "GX_CUSTOM",
            TargetServices: "Russian ISP domestic fake-host probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=hostfakesplit:host=gosuslugi.ru:tcp_ts=-1000:tcp_md5:repeats=4" },
                QuicFake6
            )
        ),

        // GX-C25: DC отечественный Gos-CH md5 → multidisorder
        new(
            Id: "GX-C25",
            Name: "DC отеч.: fake Gos-CH md5 → multidisorder",
            Tagline: "Фейк Госуслуги ClientHello + multidisorder",
            Description: "Реальный ClientHello gosuslugi.ru с md5 и autottl перед multidisorder.",
            Provenance: "GX_CUSTOM",
            TargetServices: "Discord domestic ClientHello probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_gos:tcp_md5:ip_autottl=-2,3-20:repeats=6",
                        "--lua-desync=multidisorder:pos=1,midsld" },
                QuicFake11
            )
        ),

        // GX-C26: Flowseal ALT10 Double Fake
        new(
            Id: "GX-C26",
            Name: "Flowseal ALT10: двойной fake ts",
            Tagline: "Flowseal ALT10: google + vk",
            Description: "Адаптация ALT10: двойной fake-пакет (google + vk) с tcp_ts=-1000 без сплита.",
            Provenance: "FLOWSEAL_ADAPTED",
            TargetServices: "Flowseal ALT10-style combo probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                        "--lua-desync=fake:blob=tls_vk:tcp_ts=-1000:repeats=6" },
                QuicFake6
            )
        ),

        // GX-C27: Flowseal ALT11 fake ts → seqovl
        new(
            Id: "GX-C27",
            Name: "Flowseal ALT11: fake ts → seqovl",
            Tagline: "Flowseal ALT11: fake ts + seqovl 681",
            Description: "Адаптация ALT11: fake с ts + multisplit с нахлёстом реального google ClientHello seqovl=681.",
            Provenance: "FLOWSEAL_ADAPTED",
            TargetServices: "Flowseal ALT11-style combo probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                        "--lua-desync=multisplit:pos=2:seqovl=681:seqovl_pattern=tls_google:optional" },
                QuicFake6
            )
        ),

        // GX-C28: DC fake badseq dupsid → multisplit sld+1
        new(
            Id: "GX-C28",
            Name: "DC: fake badseq dupsid → multisplit sld+1",
            Tagline: "Discord: badseq + split sld+1",
            Description: "Фейк с отрицательным sequence (-10000), autottl и dupsid перед multisplit на позиции sld+1.",
            Provenance: "YOUTUBEDISCORD_ADAPTED",
            TargetServices: "Discord TLS probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_google:tcp_seq=-10000:ip_autottl=-2,3-20:tls_mod=rnd,dupsid:repeats=2",
                        "--lua-desync=multisplit:pos=sld+1" },
                QuicFake11
            )
        ),

        // GX-C29: DC fake badsum dupsid → multidisorder sld+1
        new(
            Id: "GX-C29",
            Name: "DC: fake badsum dupsid → multidisorder sld+1",
            Tagline: "Discord: badsum + multidisorder sld+1",
            Description: "Фейк с битой контрольной суммой (badsum), autottl и dupsid перед multidisorder на позициях sld+1,midsld.",
            Provenance: "YOUTUBEDISCORD_ADAPTED",
            TargetServices: "Discord TLS probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=fake:blob=tls_google:badsum:ip_autottl=-2,3-20:tls_mod=rnd,dupsid:repeats=2",
                        "--lua-desync=multidisorder:pos=sld+1,midsld" },
                QuicFake6
            )
        ),

        // GX-C30: Clean Sequence Overlap
        new(
            Id: "GX-C30",
            Name: "SEQOVL tls_google 652 pos=2",
            Tagline: "Чистый seqovl: 652 pos=2",
            Description: "Чистый sequence-overlap с паттерном google ClientHello на позиции 2 с длиной нахлёста 652 без намеренной порчи заголовков.",
            Provenance: "UPSTREAM_ZAPRET2_ADAPTED",
            TargetServices: "TLS sequence-overlap probe",
            CompatibleLists: new[] { "discord", "youtube" },
            Args: BuildCandidateArgs(
                new[] { "--lua-desync=multisplit:pos=2:seqovl=652:seqovl_pattern=tls_google:optional" },
                QuicFake6
            )
        ),
    };

    // =========================================================================
    // Catalog Access API
    // =========================================================================

    // Adapted from bol-van/zapret2 standard blockcheck recipes, inspected 2026-10-17.
    // A recipe is a candidate, never a claim of WORK. All verbs exist in bundled v1.0.5.2.
    private static readonly ReferenceStrategy[] UpstreamCandidates =
    {
        Upstream("GX-U01", "Upstream — multisplit 1,midsld,1220", "Разрезка большого ClientHello",
            "20-multi.sh: разрезка начала, SNI и большого TLS ClientHello без фейков.",
            "--lua-desync=multisplit:pos=1,midsld,1220"),
        Upstream("GX-U02", "Upstream — multidisorder SNI", "Перестановка частей SNI",
            "20-multi.sh: несколько точек TLS/SNI без фейковых пакетов.",
            "--lua-desync=multidisorder:pos=1,sniext+1,host+1,midsld-2,midsld,midsld+2,endhost-1"),
        Upstream("GX-U03", "Upstream — hostfakesplit badack nofake2", "Hostfake с перестановкой хвоста",
            "35-hostfake.sh: disorder_after, nofake2 и badack с timestamp первым.",
            "--lua-desync=hostfakesplit:host=www.google.com:disorder_after:nofake2:tcp_ack=-66000:tcp_ts_up:repeats=2"),
        Upstream("GX-U04", "Upstream — hostfakesplit VK midhost", "Hostfake VK с разрезкой середины",
            "35-hostfake.sh: midhost + disorder_after с TCP MD5 только на фейках.",
            "--lua-desync=hostfakesplit:host=vk.com:disorder_after:midhost=midsld:tcp_md5:repeats=3"),
        Upstream("GX-U05", "Upstream — zero + TLS fake + multisplit", "Нулевой и TLS-фейк перед сплитом",
            "50-fake-multi.sh и перевод автора zapret2: нулевой фейк, dupsid и multisplit.",
            "--lua-desync=fake:blob=0x00000000:tcp_seq=-10000:repeats=2",
            "--lua-desync=fake:blob=fake_default_tls:tcp_seq=-10000:tls_mod=rnd,dupsid,sni=www.google.com:repeats=2",
            "--lua-desync=multisplit:pos=1,midsld"),
        Upstream("GX-U06", "Upstream — padencap fake + multidisorder", "TLS padding и перестановка",
            "50-fake-multi.sh: TLS padencap/dupsid, TCP MD5 и multidisorder.",
            "--lua-desync=fake:blob=fake_default_tls:tcp_md5:tls_mod=rnd,dupsid,padencap:repeats=6",
            "--lua-desync=multidisorder:pos=1,midsld"),
        Upstream("GX-U07", "Upstream — fakedsplit badack", "Ложный сплит с badack",
            "30-faked.sh: fakedsplit с badack и tcp_ts_up.",
            "--lua-desync=fakedsplit:pos=1:tcp_ack=-66000:tcp_ts_up"),
        Upstream("GX-U08", "Upstream — fakeddisorder MD5", "Ложная перестановка с MD5",
            "30-faked.sh: fakeddisorder по середине домена с TCP MD5 на фейках.",
            "--lua-desync=fakeddisorder:pos=midsld:tcp_md5:repeats=2"),
    };

    private static ReferenceStrategy Upstream(string id, string name, string tagline, string description, params string[] tls) =>
        new(id, name, tagline, description + " Совместимый кандидат; требуется проверка на вашей сети.",
            "UPSTREAM_ZAPRET2_STANDARD_20261017", "Discord + YouTube; voice unverified",
            new[] { "discord", "youtube", "exclude" }, PresetService.BuildComboArgs(tls, tls, tls));

    public static IReadOnlyList<ReferenceStrategy> BuiltIns => ShippedBuiltIns;

    public static IReadOnlyList<ReferenceStrategy> CatalogCandidates { get; } = ShippedCatalogCandidates.Concat(UpstreamCandidates).ToArray();

    /// <summary>The original 39 entries keep their IDs and argument fingerprints.</summary>
    public static IReadOnlyList<ReferenceStrategy> Baseline { get; } = ShippedBuiltIns.Concat(ShippedCatalogCandidates).ToArray();

    public static IReadOnlyList<ReferenceStrategy> Extensions => UpstreamCandidates;

    public static IReadOnlyList<ReferenceStrategy> All { get; } =
        Baseline.Concat(UpstreamCandidates).ToArray();

    public static ReferenceStrategy? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return All.FirstOrDefault(s => string.Equals(s.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static ReferenceStrategy? FindByFingerprint(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        return All.FirstOrDefault(s => string.Equals(s.Fingerprint, fingerprint.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static ReferenceStrategy? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return All.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsReferenceStrategy(string fingerprint) =>
        FindByFingerprint(fingerprint) is not null;

    public static bool IsReferenceStrategy(Preset preset)
    {
        if (preset is null) return false;
        if (!string.IsNullOrEmpty(preset.ReferenceId)) return true;
        return IsReferenceStrategy(preset.Fingerprint);
    }
}

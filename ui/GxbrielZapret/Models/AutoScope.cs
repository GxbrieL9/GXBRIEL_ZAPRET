namespace GxbrielZapret.Models;

/// <summary>What the auto-selector optimises for: a strategy that works for Discord
/// and YouTube at the same time.
/// NOTE: Telegram is intentionally NOT a scope — it isn't handled by a DPI desync at all but by the
/// built-in tg-ws-proxy (TelegramProxyService). Autoselect scores by a TLS handshake to the goal
/// host, which can't validate a proxied MTProto path anyway, so Telegram stays out of the picker.
/// NOTE: Riot/LoL uses Cloudflare CDN for its auth/config endpoints, so SNI-based TLS probes are valid.
/// Game traffic (pvp.net IPs) is routed via RiotRoutingService separately and is NOT tested here.</summary>
public enum AutoScope { Both, Discord, YouTube, Riot }

public static class AutoScopeExt
{
    public static string Title(this AutoScope s) => s switch
    {
        AutoScope.Discord => "Discord",
        AutoScope.YouTube => "YouTube",
        AutoScope.Riot => "League of Legends",
        _ => "Discord + YouTube",
    };

    /// <summary>Endpoints used to score every candidate. Strategies are never split by service.</summary>
    public static string[] GoalHosts(this AutoScope s)
    {
        // Mirror Flowseal's blockcheck targets so a passing candidate here means the same thing the
        // community calls "working": Discord Main/Gateway/CDN/Updates, YouTube Web/Short/Image/Redirect.
        // challenges.cloudflare.com = the Cloudflare Turnstile bot-challenge Discord's LOGIN loads.
        // It's in the discord hostlist, so it's probed WITH each candidate's Discord desync — a
        // candidate that doesn't pass it (TLS ok, request resets) really won't let you log in, so
        // scoring it here makes the pick reflect "login actually works", not just "the site opens".
        string[] discord = { "discord.com", "gateway.discord.gg", "cdn.discordapp.com",
                             "updates.discord.com", "challenges.cloudflare.com" };
        string[] youtube = { "www.youtube.com", "youtu.be", "i.ytimg.com", "redirector.googlevideo.com" };
        // Riot/LoL: auth and config endpoints that use Cloudflare CDN and are blocked by SNI.
        // clientconfig.rpg.riotgames.com — official League client config gate (updates/login).
        // auth.riotgames.com — Riot account authentication (Cloudflare-fronted, SNI-blocked in RU).
        // entitlements.auth.riotgames.com — required to pass entitlement check after login.
        // bacon.secure.dyn.riotcdn.net — XMPP/WebSocket presence service (Riotcdn, also SNI-checked).
        string[] riot = { "clientconfig.rpg.riotgames.com", "auth.riotgames.com",
                          "entitlements.auth.riotgames.com", "bacon.secure.dyn.riotcdn.net" };
        return s switch
        {
            AutoScope.Discord => discord,
            AutoScope.YouTube => youtube,
            AutoScope.Riot => riot,
            _ => discord.Concat(youtube).ToArray()
        };
    }
}

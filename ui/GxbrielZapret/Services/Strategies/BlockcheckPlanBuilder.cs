using System.Text;
using System.Text.RegularExpressions;
using GxbrielZapret.Models;

namespace GxbrielZapret.Services.Strategies;

public static class BlockcheckPlanBuilder
{
    public const int MaxPlans = 8;
    private sealed record Finding(string? Host, string[] Args);

    public static IReadOnlyList<Preset> Build(string output, AutoScope scope, bool disableQuic = false)
    {
        var domains = scope.GoalHosts().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var findings = new List<Finding>();
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("* ", StringComparison.Ordinal)) { section = line; continue; }
            if (section is not ("* SUMMARY" or "* COMMON")) continue;
            var prefix = Regex.Match(line, @"^(?:curl_test_)?https_tls1[23]\s+ipv4\s*(?<host>[a-zA-Z0-9.-]+)?\s*:\s*(?<value>.+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!prefix.Success) continue;
            string? host = section == "* SUMMARY" ? prefix.Groups["host"].Value.ToLowerInvariant() : null;
            if (host is not null && !domains.Contains(host)) continue;
            string value = prefix.Groups["value"].Value;
            if (host is not null && value == "working without bypass") { direct.Add(host); continue; }
            var command = Regex.Match(value, @"^(?:winws2|nfqws2)(?:\.exe)?\s+(.+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!command.Success) continue;
            var args = ParseArguments(command.Groups[1].Value);
            if (args is not null) findings.Add(new(host, args));
        }

        var result = new List<Preset>();
        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string[]? discord, string[]? youtube)
        {
            // Lua initializers share one global state; different initializers cannot be merged safely.
            if (discord is not null && youtube is not null &&
                !discord.Where(a => a.StartsWith("--lua-init=", StringComparison.Ordinal))
                    .SequenceEqual(youtube.Where(a => a.StartsWith("--lua-init=", StringComparison.Ordinal)), StringComparer.Ordinal)) return;
            var args = new List<string> { "--wf-tcp-out=443" };
            if (disableQuic) args.Add("--wf-udp-out=443");
            bool hasRule = false;
            void Rule(string name, string[]? recipe)
            {
                if (recipe is null) return;
                if (hasRule) args.Add("--new");
                hasRule = true;
                args.AddRange(new[] { "--filter-tcp=443", "{HOSTLIST:" + name + "}" });
                args.AddRange(recipe);
            }
            Rule("discord", discord);
            Rule("youtube", youtube);
            if (disableQuic)
            {
                foreach (string service in new[] { "discord", "youtube" })
                {
                    if (service == "discord" && discord is null || service == "youtube" && youtube is null) continue;
                    args.AddRange(new[] { "--new", "--filter-udp=443", "--filter-l7=quic", "{HOSTLIST:" + service + "}", "--payload=quic_initial", "--lua-desync=drop" });
                }
            }
            string fingerprint = CanonicalProfile.ComputeFingerprint(args);
            if (!fingerprints.Add(fingerprint) || result.Count >= MaxPlans) return;
            result.Add(new Preset
            {
                Name = "blockcheck2 candidate " + (result.Count + 1),
                Args = args, Fingerprint = fingerprint, UsesHostlist = true,
                Provenance = "UPSTREAM_BLOCKCHECK2", Status = "UNTESTED",
                Reason = "blockcheck2_requires_application_verification", TargetServices = scope.Title(),
                CompatibleLists = new() { "discord", "youtube" }
            });
        }

        // Prefer reported common recipes, then combine independently discovered service recipes.
        foreach (var common in findings.Where(f => f.Host is null).DistinctBy(f => CanonicalProfile.ComputeFingerprint(f.Args)).Take(2))
            Add(scope == AutoScope.YouTube ? null : common.Args, scope == AutoScope.Discord ? null : common.Args);

        IReadOnlyList<string[]> Choices(AutoScope service)
        {
            var hosts = service.GoalHosts();
            var choices = findings.Where(f => f.Host is not null && hosts.Contains(f.Host, StringComparer.OrdinalIgnoreCase))
                .GroupBy(f => CanonicalProfile.ComputeFingerprint(f.Args))
                .OrderByDescending(group => group.Select(f => f.Host).Distinct().Count())
                .Select(group => group.First().Args).Take(3).ToList();
            if (hosts.All(direct.Contains)) choices.Insert(0, Array.Empty<string>());
            return choices.Take(3).ToArray();
        }
        var dc = scope == AutoScope.YouTube ? new[] { Array.Empty<string>() } : Choices(AutoScope.Discord);
        var yt = scope == AutoScope.Discord ? new[] { Array.Empty<string>() } : Choices(AutoScope.YouTube);
        foreach (var discord in dc)
            foreach (var youtube in yt)
                Add(scope == AutoScope.YouTube ? null : discord, scope == AutoScope.Discord ? null : youtube);
        return result;
    }

    private static string[]? ParseArguments(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        bool preserveQuote = false;
        foreach (char c in command)
        {
            if (quote != '\0') { if (c == quote) { if (preserveQuote) current.Append(c); quote = '\0'; } else current.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; preserveQuote = current.Length > 0; if (preserveQuote) current.Append(c); continue; }
            if (char.IsWhiteSpace(c)) { if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); } }
            else current.Append(c);
        }
        if (quote != '\0') return null;
        if (current.Length > 0) tokens.Add(current.ToString());
        // Retain only relocatable recipe arguments, never capture filters, shell syntax or file paths.
        var recipe = tokens.Where(a => !a.StartsWith("--wf-", StringComparison.Ordinal)).ToArray();
        if (!recipe.Any(a => a.StartsWith("--lua-desync=", StringComparison.Ordinal)) || recipe.Any(a =>
            !(a.StartsWith("--lua-desync=", StringComparison.Ordinal) || a.StartsWith("--lua-init=", StringComparison.Ordinal) ||
              a.StartsWith("--payload=", StringComparison.Ordinal) || a.StartsWith("--out-range=", StringComparison.Ordinal)) ||
            a.Contains('@') || a.Contains('"') || a.Contains("$", StringComparison.Ordinal) || a.Contains('`'))) return null;
        return recipe;
    }
}

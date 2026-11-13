using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace GxbrielZapret.Services.Engine.Discovery;

/// <summary>
/// Normalization rules for domains, IPv4, IPv6, and CIDR subnets.
/// Guarantees canonical, deduplicated, and safe endpoint representation.
/// </summary>
public static class EndpointNormalizer
{
    private static readonly IdnMapping Idn = new();

    /// <summary>
    /// Normalizes a domain name:
    /// - trims whitespace
    /// - converts to lowercase
    /// - strips scheme (http://, https://, ws://, etc.)
    /// - strips path, query, and fragment (/..., ?..., #...)
    /// - strips port (:443, :80, etc.)
    /// - removes wildcard prefixes (*.)
    /// - removes trailing dot (FQDN root)
    /// - canonicalizes internationalized domain names (IDN/Punycode)
    /// - validates characters and length
    /// </summary>
    public static bool TryNormalizeDomain(string? raw, out string canonicalDomain, out string? error)
    {
        canonicalDomain = "";
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Domain cannot be null or empty";
            return false;
        }

        string s = raw.Trim().ToLowerInvariant();

        // 1. Strip scheme
        int schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
            s = s[(schemeIdx + 3)..];

        // 2. Strip path, query, hash, fragment, backslash
        int slashIdx = s.IndexOfAny(new[] { '/', '\\', '?', '#' });
        if (slashIdx >= 0)
            s = s[..slashIdx];

        // 3. Strip port
        int colonIdx = s.LastIndexOf(':');
        if (colonIdx > 0 && !s.Contains(']')) // avoid stripping IPv6 bracketed colons
        {
            string portPart = s[(colonIdx + 1)..];
            if (int.TryParse(portPart, out _) || portPart == "*")
            {
                s = s[..colonIdx];
            }
        }

        // 4. Strip wildcard prefixes (*. or .)
        while (s.StartsWith("*.", StringComparison.Ordinal) || s.StartsWith('.'))
        {
            s = s.StartsWith("*.", StringComparison.Ordinal) ? s[2..] : s[1..];
        }

        // 5. Strip trailing dots and spaces
        s = s.Trim().TrimEnd('.');

        if (string.IsNullOrWhiteSpace(s))
        {
            error = "Domain became empty after stripping delimiters";
            return false;
        }

        // 6. Check for invalid characters
        if (s.Contains(' ') || s.Contains('/') || s.Contains('\\') || s.Contains(':') || s.Contains('@'))
        {
            error = $"Domain contains invalid characters: '{s}'";
            return false;
        }

        // 7. Canonicalize IDN (Punycode / ASCII)
        try
        {
            s = Idn.GetAscii(s).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            error = $"Invalid IDN domain '{s}': {ex.Message}";
            return false;
        }

        // Check if this is an IP address rather than a domain name
        if (IPAddress.TryParse(s, out _))
        {
            error = $"'{s}' is an IP address, not a domain";
            return false;
        }

        var labels = s.Split('.');
        if (labels.Length < 2)
        {
            // Must have at least two labels for a routable FQDN
            error = "Domain must contain at least two labels separated by dot";
            return false;
        }

        // TLD cannot be purely numeric (RFC 1123)
        if (labels[^1].All(char.IsDigit))
        {
            error = $"TLD '{labels[^1]}' cannot be purely numeric";
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length == 0)
            {
                error = "Domain contains empty label (consecutive dots)";
                return false;
            }
            if (label.Length > 63)
            {
                error = $"Domain label '{label}' exceeds 63 characters";
                return false;
            }
            if (label.StartsWith('-') || label.EndsWith('-'))
            {
                error = $"Domain label '{label}' cannot start or end with hyphen";
                return false;
            }
            if (!Regex.IsMatch(label, @"^[a-z0-9\-_]+$"))
            {
                error = $"Domain label '{label}' contains illegal characters";
                return false;
            }
        }

        canonicalDomain = s;
        return true;
    }

    /// <summary>
    /// Normalizes an IPv4 or IPv6 address.
    /// Checks for public/routable address and rejects loopback, private, unspecified, and multicast.
    /// </summary>
    public static bool TryNormalizeIp(string? raw, out string canonicalIp, out string? error)
    {
        canonicalIp = "";
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "IP address cannot be null or empty";
            return false;
        }

        string s = raw.Trim();

        // Strip port if IPv4 (e.g. 1.2.3.4:443)
        int colonIdx = s.LastIndexOf(':');
        if (colonIdx > 0 && !s.Contains('[') && !s.Contains("::") && s.Count(c => c == ':') == 1)
        {
            if (int.TryParse(s[(colonIdx + 1)..], out _))
                s = s[..colonIdx];
        }

        // Strip IPv6 brackets e.g. [2001:db8::1]:443 -> 2001:db8::1
        if (s.StartsWith('[') && s.Contains(']'))
        {
            int closeBracket = s.IndexOf(']');
            s = s.Substring(1, closeBracket - 1);
        }

        if (!IPAddress.TryParse(s, out var ip))
        {
            error = $"'{raw}' is not a valid IP address";
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();

            // Check non-public IPv4
            if (b[0] == 0) { error = "Unspecified IPv4 (0.0.0.0/8)"; return false; }
            if (b[0] == 10) { error = "Private IPv4 (10.0.0.0/8)"; return false; }
            if (b[0] == 127) { error = "Loopback IPv4 (127.0.0.0/8)"; return false; }
            if (b[0] == 169 && b[1] == 254) { error = "Link-local IPv4 (169.254.0.0/16)"; return false; }
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) { error = "Private IPv4 (172.16.0.0/12)"; return false; }
            if (b[0] == 192 && b[1] == 168) { error = "Private IPv4 (192.168.0.0/16)"; return false; }
            if (b[0] >= 224 && b[0] <= 239) { error = "Multicast IPv4 (224.0.0.0/4)"; return false; }
            if (b[0] >= 240) { error = "Reserved/Broadcast IPv4 (240.0.0.0/4)"; return false; }

            canonicalIp = ip.ToString();
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(ip)) { error = "Loopback IPv6 (::1)"; return false; }
            if (IPAddress.IPv6None.Equals(ip) || IPAddress.IPv6Any.Equals(ip)) { error = "Unspecified IPv6 (::)"; return false; }
            if (ip.IsIPv6LinkLocal) { error = "Link-local IPv6 (fe80::/10)"; return false; }
            if (ip.IsIPv6Multicast) { error = "Multicast IPv6 (ff00::/8)"; return false; }
            if (ip.IsIPv6SiteLocal) { error = "Site-local IPv6"; return false; }

            byte[] b = ip.GetAddressBytes();
            // Unique local address (fc00::/7)
            if ((b[0] & 0xfe) == 0xfc) { error = "Unique local IPv6 (fc00::/7)"; return false; }

            canonicalIp = ip.ToString();
            return true;
        }

        error = "Unsupported address family";
        return false;
    }

    /// <summary>
    /// Normalizes a CIDR string (e.g. 1.2.3.4/24 -> 1.2.3.0/24).
    /// Computes the true canonical network address by masking host bits.
    /// </summary>
    public static bool TryNormalizeCidr(string? raw, out string canonicalCidr, out string? error)
    {
        canonicalCidr = "";
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "CIDR cannot be null or empty";
            return false;
        }

        string s = raw.Trim();
        int slashIdx = s.IndexOf('/');
        string ipPart;
        int prefix;

        if (slashIdx >= 0)
        {
            ipPart = s[..slashIdx].Trim();
            string prefixStr = s[(slashIdx + 1)..].Trim();
            if (!int.TryParse(prefixStr, out prefix))
            {
                error = $"Invalid CIDR prefix '{prefixStr}'";
                return false;
            }
        }
        else
        {
            ipPart = s;
            prefix = ipPart.Contains(':') ? 128 : 32;
        }

        if (!IPAddress.TryParse(ipPart, out var ip))
        {
            error = $"'{ipPart}' is not a valid IP in CIDR";
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (prefix < 0 || prefix > 32)
            {
                error = $"IPv4 prefix {prefix} must be between 0 and 32";
                return false;
            }

            byte[] bytes = ip.GetAddressBytes();
            uint ipUint = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
            uint mask = prefix == 0 ? 0 : (~0u << (32 - prefix));
            uint netUint = ipUint & mask;

            byte[] netBytes = new byte[4]
            {
                (byte)((netUint >> 24) & 0xff),
                (byte)((netUint >> 16) & 0xff),
                (byte)((netUint >> 8) & 0xff),
                (byte)(netUint & 0xff)
            };

            var netIp = new IPAddress(netBytes);
            canonicalCidr = $"{netIp}/{prefix}";
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (prefix < 0 || prefix > 128)
            {
                error = $"IPv6 prefix {prefix} must be between 0 and 128";
                return false;
            }

            byte[] bytes = ip.GetAddressBytes();
            int fullBytes = prefix / 8;
            int remainingBits = prefix % 8;

            for (int i = fullBytes + 1; i < bytes.Length; i++)
            {
                bytes[i] = 0;
            }

            if (fullBytes < bytes.Length && remainingBits > 0)
            {
                byte mask = (byte)(0xff << (8 - remainingBits));
                bytes[fullBytes] &= mask;
            }

            var netIp = new IPAddress(bytes);
            canonicalCidr = $"{netIp}/{prefix}";
            return true;
        }

        error = "Unsupported address family";
        return false;
    }
}

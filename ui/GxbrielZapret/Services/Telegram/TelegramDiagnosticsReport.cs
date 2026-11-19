using System.Text;

namespace GxbrielZapret.Services.Telegram;

public sealed class TelegramProbeStep
{
    public string TargetName { get; set; } = "";
    public int Dc { get; set; }
    public string Lane { get; set; } = "chat";
    public string DnsSource { get; set; } = "";
    public string AttemptedEndpoint { get; set; } = "";
    public bool DnsOk { get; set; } = true;
    public string? DnsError { get; set; }
    public bool TcpOk { get; set; }
    public bool TlsOk { get; set; }
    public string? TlsError { get; set; }
    public int WsStatus { get; set; }
    public bool WsOk { get; set; }
    public string? WsError { get; set; }
    public bool MtProtoResPqOk { get; set; }
    public long LatencyMs { get; set; }
    public string OutcomeSummary { get; set; } = "";
}

public sealed class TelegramDiagnosticsReport
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool OverallOk { get; set; }
    public string Summary { get; set; } = "";
    public List<TelegramProbeStep> Steps { get; set; } = new();

    public string ToSafeDiagnosticText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== GXBRIEL_ZAPRET Telegram Diagnostics ({TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC) ===");
        sb.AppendLine($"Overall: {(OverallOk ? "PASS" : "FAIL")} — {Summary}");
        sb.AppendLine("Steps:");
        foreach (var s in Steps)
        {
            sb.Append($"• [{s.TargetName}] DC{s.Dc} ({s.Lane}) via {s.DnsSource} -> {s.AttemptedEndpoint}: ");
            if (!s.DnsOk)
            {
                sb.AppendLine($"DNS FAIL ({s.DnsError ?? "no record"})");
                continue;
            }
            if (!s.TcpOk)
            {
                sb.AppendLine("TCP FAIL (timeout / blocked)");
                continue;
            }
            if (!s.TlsOk)
            {
                sb.AppendLine($"TLS FAIL ({s.TlsError ?? "handshake failed"})");
                continue;
            }
            if (!s.WsOk)
            {
                sb.AppendLine($"WS FAIL (status {s.WsStatus}: {s.WsError ?? "upgrade rejected"})");
                continue;
            }
            if (!s.MtProtoResPqOk)
            {
                sb.AppendLine("MTProto FAIL (WS 101 OK, but no valid resPQ response in 5s)");
                continue;
            }
            sb.AppendLine($"OK (WS 101 + resPQ confirmed, {s.LatencyMs} ms)");
        }
        sb.AppendLine("==================================================================");
        return sb.ToString();
    }
}

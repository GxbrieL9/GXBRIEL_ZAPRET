using System.IO;
using System.Text;

namespace GxbrielZapret.Services.Platform;

internal enum AntigravityRouteFeedbackResult { None, SwitchRoute, Exhausted }

// Only newly appended error records affect routing. Prompts, tokens and raw records never leave here.
internal sealed class AntigravityRouteFeedback
{
    private readonly string _path;
    private readonly Func<long> _clock;
    private long _offset, _windowStart, _nextSwitchAt;
    private int _switches;
    private string _partial = "";

    internal AntigravityRouteFeedback(string path, Func<long>? clock = null)
    {
        _path = path; _clock = clock ?? (() => Environment.TickCount64);
        Reset();
    }

    internal void Reset()
    {
        try { _offset = File.Exists(_path) ? new FileInfo(_path).Length : 0; }
        catch { _offset = 0; }
        _partial = ""; _windowStart = _clock(); _nextSwitchAt = 0; _switches = 0;
    }

    internal AntigravityRouteFeedbackResult Poll()
    {
        try
        {
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < _offset) { _offset = 0; _partial = ""; }
            if (file.Length == _offset) return AntigravityRouteFeedbackResult.None;
            long position = Math.Max(_offset, file.Length - 65536);
            if (position != _offset) _partial = "";
            file.Position = position;
            byte[] bytes = new byte[(int)Math.Min(file.Length - position, 65536)];
            int read = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            _offset = file.Position;
            string text = _partial + Encoding.UTF8.GetString(bytes, 0, read);
            int lastLine = text.LastIndexOf('\n');
            _partial = lastLine >= 0 ? text[(lastLine + 1)..] : text;
            if (_partial.Length > 1024) _partial = _partial[^1024..];
            if (lastLine < 0 || !HasRegionFailure(text[..(lastLine + 1)])) return AntigravityRouteFeedbackResult.None;
            long now = _clock();
            if (now < _nextSwitchAt) return AntigravityRouteFeedbackResult.None;
            if (now - _windowStart >= 120000) { _windowStart = now; _switches = 0; }
            _nextSwitchAt = now + 30000;
            if (_switches >= AntigravitySmartDns.Providers.Length - 1) return AntigravityRouteFeedbackResult.Exhausted;
            _switches++;
            return AntigravityRouteFeedbackResult.SwitchRoute;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return AntigravityRouteFeedbackResult.None; }
    }

    internal static bool HasRegionFailure(string text) => text.Split('\n').Any(line =>
        (line.Contains("User location is not supported", StringComparison.OrdinalIgnoreCase) ||
         line.Contains("LOCATION_NOT_ELIGIBLE", StringComparison.OrdinalIgnoreCase)) &&
        (line.Contains("FAILED_PRECONDITION", StringComparison.OrdinalIgnoreCase) || line.Contains("400", StringComparison.Ordinal) || line.Contains("403", StringComparison.Ordinal)));
}

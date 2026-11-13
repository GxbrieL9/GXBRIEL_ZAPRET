using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;

namespace GxbrielZapret.Services.Platform;

// The CLI checks LoadCodeAssistResponse before constructing the UI-facing AuthResult.
// Require both the exact field-5 descriptor/type and its matching Go/JSON tag.
internal static class AntigravityCodeAssistSchema
{
    internal const string Descriptor = "\n\x10ineligible_tiers\x18\x05 \x03(\x0b" +
        "25.google.internal.cloud.code.v1internal.IneligibleTierR\x0fineligibleTiers";
    internal const string Tag = "protobuf:\"bytes,5,rep,name=ineligible_tiers,json=ineligibleTiers,proto3\" json:\"ineligible_tiers,omitempty\"";
    private static readonly string[] Anchors = [Descriptor, Tag];
    private static string Modified(string anchor) => anchor.Replace("ineligible", "gxeligible", StringComparison.Ordinal);

    internal static AntigravityPatchPlan? Analyze(Stream stream, PEHeaders headers)
    {
        var original = Anchors.Select(_ => new HashSet<long>()).ToArray();
        var modified = Anchors.Select(_ => new HashSet<long>()).ToArray();
        byte[] buffer = new byte[65536 + 256];
        foreach (var section in headers.SectionHeaders.Where(s =>
                     (s.SectionCharacteristics & SectionCharacteristics.ContainsInitializedData) != 0 &&
                     (s.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0))
        {
            long start = section.PointerToRawData, end = start + section.SizeOfRawData;
            if (start < 0 || end > stream.Length) return new("Unknown", "Повреждённая секция LoadCodeAssistResponse; запись запрещена.", []);
            for (long offset = start; offset < end; offset += 65536)
            {
                stream.Position = offset;
                int count = (int)Math.Min(buffer.Length, end - offset);
                stream.ReadExactly(buffer.AsSpan(0, count));
                string text = Encoding.Latin1.GetString(buffer, 0, count);
                for (int i = 0; i < Anchors.Length; i++)
                {
                    Find(text, Anchors[i], offset, original[i]);
                    Find(text, Modified(Anchors[i]), offset, modified[i]);
                }
            }
        }
        if (original.All(h => h.Count == 0) && modified.All(h => h.Count == 0)) return null;
        if (Enumerable.Range(0, Anchors.Length).Any(i => original[i].Count + modified[i].Count != 1))
            return new("Unknown", "Схема LoadCodeAssistResponse неоднозначна или неполна. Запись запрещена.", []);
        var changes = original.SelectMany((matches, i) => matches.Select(offset =>
            new AntigravityByteChange(offset, Encoding.ASCII.GetBytes(Modified(Anchors[i]))))).ToArray();
        return new(changes.Length == 0 ? "Patched" : changes.Length == Anchors.Length ? "Unpatched" : "Partial",
            "LoadCodeAssistResponse: проверены поле 5 IneligibleTier, JSON-имя и Go/JSON-тег (5 имён в 2 структурах).", changes);
    }

    private static void Find(string text, string needle, long offset, HashSet<long> matches)
    {
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            matches.Add(offset + at);
            if (matches.Count > 1) return;
        }
    }
}

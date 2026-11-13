using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;

namespace GxbrielZapret.Services.Platform;

// Match the protobuf field's structure, not compiler-dependent code addresses.
// All three representations must agree. Never replace arbitrary occurrences of a word.
internal static class AntigravityRegionSchema
{
    private static readonly (string Prefix, string Suffix)[] Anchors =
    [
        ("\n\n", "\x18\x03"),
        ("R\n", "\x12"),
        ("name=", ",proto3,oneof")
    ];
    private const string Original = "ineligible";
    private const string Modified = "gxeligible";

    internal static AntigravityPatchPlan? Analyze(Stream stream, PEHeaders headers)
    {
        var original = Anchors.Select(_ => new HashSet<long>()).ToArray();
        var modified = Anchors.Select(_ => new HashSet<long>()).ToArray();
        byte[] buffer = new byte[65536 + 64];
        foreach (var section in headers.SectionHeaders.Where(s =>
                     (s.SectionCharacteristics & SectionCharacteristics.ContainsInitializedData) != 0 &&
                     (s.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0))
        {
            long start = section.PointerToRawData, end = start + section.SizeOfRawData;
            if (start < 0 || end > stream.Length)
                return new("Unknown", "Повреждённая секция данных protobuf; запись запрещена.", []);
            for (long offset = start; offset < end; offset += 65536)
            {
                stream.Position = offset;
                int count = (int)Math.Min(buffer.Length, end - offset);
                stream.ReadExactly(buffer.AsSpan(0, count));
                string text = Encoding.Latin1.GetString(buffer, 0, count);
                for (int i = 0; i < Anchors.Length; i++)
                {
                    Find(text, Anchors[i].Prefix + Original + Anchors[i].Suffix, offset, Anchors[i].Prefix.Length, original[i]);
                    Find(text, Anchors[i].Prefix + Modified + Anchors[i].Suffix, offset, Anchors[i].Prefix.Length, modified[i]);
                }
            }
        }
        if (original.All(h => h.Count == 0) && modified.All(h => h.Count == 0)) return null;
        if (Enumerable.Range(0, Anchors.Length).Any(i => original[i].Count + modified[i].Count != 1))
            return new("Unknown", "Схема регионального поля неоднозначна или изменилась: нужны все три согласованные точки. Файл не изменён.", []);
        var changes = original.SelectMany(h => h.Select(offset => new AntigravityByteChange(offset, Encoding.ASCII.GetBytes(Modified)))).ToArray();
        return new(changes.Length == 0 ? "Patched" : changes.Length == 3 ? "Unpatched" : "Partial",
            "Структурное правило AuthResult: подтверждены descriptor, JSON-имя и Go-тег (3/3). Работа входа и модели ещё не проверена.", changes);
    }

    private static void Find(string text, string needle, long offset, int prefix, HashSet<long> matches)
    {
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            matches.Add(offset + at + prefix);
            if (matches.Count > 1) return;
        }
    }
}

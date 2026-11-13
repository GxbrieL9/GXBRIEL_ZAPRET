using System.Text;

namespace GxbrielZapret.Services.Platform;

// Observed bundled agy extension 1.11.0. Renamed/minified builds must be inspected again.
// Keep first installation, minimum-version validation and download integrity checks intact.
internal static class AntigravityHubInstaller
{
    internal const string Validation = "let fb=b(await c(Ib(E,Va.MIN_AGY_VERSION,g,{forceVersionCheck:v}))),nb=fb.valid===!0||fb.version!==void 0,Ta=fb.version";
    internal const string Acquisition = "let Ja=b(await c(Mc({existingBinaryValid:nb,existingValidVersion:Ta,releaseBaseUrl:Xa,retryOptions:t,outputChannel:g})));";
    internal const string Guard = "/* GXBRIEL_AG_VALIDATED_HUB_BINARY_V1 */if(fb.valid===!0&&typeof fb.version===\"string\"&&fb.version.length>0){g.appendLine(\"[INSTALL] GX: using validated installed binary; update check skipped.\");return E;}";

    internal static AntigravityPatchPlan? Analyze(string text)
    {
        if (!text.Contains(Validation, StringComparison.Ordinal) && !text.Contains("GXBRIEL_AG_VALIDATED_HUB_BINARY_V1", StringComparison.Ordinal)) return null;
        static int Count(string value, string needle) => (value.Length - value.Replace(needle, "", StringComparison.Ordinal).Length) / needle.Length;
        if (Count(text, Validation) != 1 || Count(text, Acquisition) != 1 ||
            Count(text, "Va.checkBinaryVersion=Ib;") != 1 || Count(text, "Va.acquireInstalledBinaryPath=Oc;") != 1)
            return new("Unknown", "Структура установщика Hub изменилась или неоднозначна. Файл не изменён.", []);
        int anchor = text.IndexOf(Acquisition, StringComparison.Ordinal);
        bool patched = anchor >= Guard.Length && text.AsSpan(anchor - Guard.Length, Guard.Length).SequenceEqual(Guard);
        if (text.Contains("GXBRIEL_AG_VALIDATED_HUB_BINARY_V1", StringComparison.Ordinal) && (!patched || Count(text, Guard) != 1))
            return new("Unknown", "Маркер патча Hub не соответствует проверенному правилу. Запись запрещена.", []);
        return new(patched ? "Patched" : "Unpatched",
            "Правило установщика Hub: использовать установленный CLI только после успешной проверки минимальной версии. " +
            "Первичная загрузка CLI требует сети. Это не региональный патч и не проверка ответа модели.", [],
            patched ? null : new UTF8Encoding(false, true).GetBytes(text.Insert(anchor, Guard)), "hub-installer-1.11-v1");
    }
}

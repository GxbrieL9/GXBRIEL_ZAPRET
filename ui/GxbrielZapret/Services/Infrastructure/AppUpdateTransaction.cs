using System.IO;
using System.Security.Cryptography;

namespace GxbrielZapret.Services.Infrastructure;

internal static class AppUpdateTransaction
{
    internal static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    internal static async Task ApplyAsync(string target, string source, string expectedOldHash, string expectedNewHash, Func<Task<bool>> probe)
    {
        AppUpdateInstaller.RejectLinks(target);
        if (Hash(target) != expectedOldHash || Hash(source) != expectedNewHash) throw new IOException("Файлы изменились во время подготовки обновления.");
        string pending = target + ".update-" + Guid.NewGuid().ToString("N");
        string backup = target + ".previous-" + Guid.NewGuid().ToString("N");
        File.Copy(source, pending);
        if (Hash(pending) != expectedNewHash) throw new IOException("Повреждена подготовленная копия обновления.");
        if (Hash(target) != expectedOldHash) throw new IOException("Приложение изменилось перед заменой.");
        File.Replace(pending, target, backup);
        try
        {
            if (Hash(target) != expectedNewHash || !await probe()) throw new IOException("Новая версия не подтвердила запуск. Восстановлена предыдущая версия.");
        }
        catch
        {
            if (Hash(backup) != expectedOldHash) throw new IOException("Копия для отката повреждена. Автоматический запуск запрещён.");
            File.Copy(backup, pending);
            File.Replace(pending, target, null);
            if (Hash(target) != expectedOldHash) throw new IOException("Не удалось подтвердить откат.");
            throw;
        }
    }
}

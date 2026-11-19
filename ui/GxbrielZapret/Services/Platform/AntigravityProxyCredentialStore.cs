using System.Security.Cryptography;
using System.Text;

namespace GxbrielZapret.Services.Platform;

public sealed class AntigravityCredentialException : Exception
{
    public AntigravityCredentialException(string message, Exception? inner = null) : base(message, inner) { }
}

internal static class AntigravityProxyCredentialStore
{
    private static readonly byte[] Entropy = "GxbrielZapret.AntigravityProxy.Entropy.v1"u8.ToArray();

    // Internal hook for testing simulated DPAPI failure
    internal static bool SimulateProtectionFailure { get; set; }

    internal static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        if (SimulateProtectionFailure)
            throw new AntigravityCredentialException("Не удалось безопасно зашифровать учётные данные прокси через Windows DPAPI. Значение скрыто.",
                new CryptographicException("Simulated DPAPI failure"));
        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipherBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            throw new AntigravityCredentialException("Не удалось безопасно зашифровать учётные данные прокси через Windows DPAPI. Значение скрыто.", ex);
        }
    }

    internal static string? Unprotect(string? cipherBase64)
    {
        if (string.IsNullOrWhiteSpace(cipherBase64)) return null;
        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherBase64.Trim());
            byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace MyBackup.Core.Config;

/// <summary>
/// Шифрование паролей в файле настроек через DPAPI (область LocalMachine):
/// расшифровать значение можно только на этом же компьютере.
/// </summary>
public static class Secrets
{
    public const string Prefix = "dpapi:";

    private static readonly byte[] Entropy = "MyBackup.v1"u8.ToArray();

    public static string Protect(string plain)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(bytes);
    }

    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Reveal(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!IsProtected(value)) return value;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy,
                DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException e)
        {
            // "dpapi:ЗАМЕНИТЕ" из примера настроек — заготовка, а не зашифрованное значение.
            throw new ConfigException("Значение не заполнено (в настройках заготовка из примера) — введите его.", e);
        }
        catch (CryptographicException e)
        {
            throw new ConfigException(
                "Не удалось расшифровать значение dpapi: оно создано на другом компьютере. Введите его заново.", e);
        }
    }
}

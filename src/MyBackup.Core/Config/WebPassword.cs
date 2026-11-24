using System.Security.Cryptography;
using System.Text;

namespace MyBackup.Core.Config;

/// <summary>Хеш пароля веб-интерфейса (PBKDF2-SHA256).</summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Scheme = "pbkdf2-sha256";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Пароль веб-интерфейса хранится отдельно от config.json (в папке данных, доступ только у SYSTEM
/// и администраторов): смена пароля не переписывает файл настроек.
/// </summary>
public sealed class WebPasswordStore(string path)
{
    public const int MinLength = 8;

    public string FilePath => path;

    public bool IsSet => Read() is not null;

    public string? Read()
    {
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            return text.Length > 0 ? text : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public bool Verify(string password) => PasswordHasher.Verify(password, Read());

    /// <summary>Короткий отпечаток текущего хеша: после смены пароля старые сессии перестают действовать.</summary>
    public string Stamp()
    {
        var hash = Read() ?? "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash)))[..16];
    }

    public void Set(string password)
    {
        if (password.Length < MinLength)
            throw new ArgumentException($"Пароль должен быть не короче {MinLength} символов");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, PasswordHasher.Hash(password));
        File.Move(temp, path, overwrite: true);
    }
}

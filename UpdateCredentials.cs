using System.Security.Cryptography;
using System.Text;

namespace NetScope;

static class UpdateCredentials
{
    static string FilePath => Path.Combine(Preferences.DirectoryPath, "github-update-access.dat");

    public static string? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
    }

    public static void Save(string token)
    {
        token = token.Trim();
        if (token.Length < 20 || token.Any(char.IsWhiteSpace)) throw new ArgumentException("Paste a valid GitHub access token.");
        Directory.CreateDirectory(Preferences.DirectoryPath);
        byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        string temporary = FilePath + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, FilePath, true);
    }

    public static void Clear() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}

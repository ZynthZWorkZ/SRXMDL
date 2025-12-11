using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SRXMDL.Login
{
    public static class CredentialStore
    {
        private static readonly string CredPath = Path.Combine(AppContext.BaseDirectory, "Login", "cred.json");

        public static (string Email, string Password)? Load()
        {
            try
            {
                if (!File.Exists(CredPath)) return null;
                var json = File.ReadAllText(CredPath);
                var cred = JsonSerializer.Deserialize<LoginCred>(json);
                if (cred == null || string.IsNullOrWhiteSpace(cred.Email) || string.IsNullOrWhiteSpace(cred.PasswordProtected))
                    return null;

                var decrypted = Decrypt(cred.PasswordProtected);
                if (decrypted == null) return null;

                return (cred.Email, decrypted);
            }
            catch
            {
                return null;
            }
        }

        public static bool Save(string email, string password)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                    return false;

                var encrypted = Encrypt(password);
                if (encrypted == null) return false;

                var cred = new LoginCred
                {
                    Email = email.Trim(),
                    PasswordProtected = encrypted
                };

                Directory.CreateDirectory(Path.GetDirectoryName(CredPath)!);
                var json = JsonSerializer.Serialize(cred, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(CredPath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(CredPath))
                {
                    File.Delete(CredPath);
                }
            }
            catch
            {
                // ignore
            }
        }

        private static string? Encrypt(string plain)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(plain);
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protectedBytes);
            }
            catch
            {
                return null;
            }
        }

        private static string? Decrypt(string cipher)
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(cipher);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }
    }
}


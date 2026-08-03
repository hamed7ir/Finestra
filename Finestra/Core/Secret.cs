using System;
using System.Security.Cryptography;
using System.Text;

namespace Finestra.Core
{
    /// <summary>
    /// DPAPI wrapper for saved passwords. Uses <see cref="ProtectedData"/> with
    /// <see cref="DataProtectionScope.CurrentUser"/> so the ciphertext is bound to THIS Windows user on THIS
    /// machine — it can't be decrypted by another user, on another machine, or lifted from the JSON file.
    /// Base64 in/out for JSON storage. Never throws: any failure returns "" (the app treats that as "no
    /// password" and prompts/omits rather than crashing). RT-safe: DPAPI (crypt32) is present on RT 8.1.
    /// </summary>
    public static class Secret
    {
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch { return ""; }
        }

        public static string Unprotect(string enc)
        {
            if (string.IsNullOrEmpty(enc)) return "";
            try
            {
                byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return ""; }
        }
    }
}

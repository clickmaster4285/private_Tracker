using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace client.Services.FaceVerify;

/// <summary>
/// DPAPI CurrentUser envelope for local template blobs. Soft protection against
/// casual offline theft — not a substitute for full-disk encryption / TPM.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FaceDpapi
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("AATFDP1\0");

    public static byte[] Protect(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
            return plain;
        var protectedBytes = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var outBuf = new byte[Magic.Length + protectedBytes.Length];
        Buffer.BlockCopy(Magic, 0, outBuf, 0, Magic.Length);
        Buffer.BlockCopy(protectedBytes, 0, outBuf, Magic.Length, protectedBytes.Length);
        return outBuf;
    }

    public static byte[] Unprotect(byte[] stored)
    {
        if (stored.Length >= Magic.Length && stored.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            if (!OperatingSystem.IsWindows())
                throw new CryptographicException("DPAPI blob on non-Windows");
            var cipher = stored.AsSpan(Magic.Length).ToArray();
            return ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        // Legacy plaintext float32 blob
        return stored;
    }

    public static bool IsProtected(byte[] stored) =>
        stored.Length >= Magic.Length && stored.AsSpan(0, Magic.Length).SequenceEqual(Magic);
}

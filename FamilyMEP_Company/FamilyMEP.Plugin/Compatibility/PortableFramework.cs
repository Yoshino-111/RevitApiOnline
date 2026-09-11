using System.Security.Cryptography;
using System.Text;

namespace FamilyMEP.Plugin.Compatibility;

internal static class PortableFramework
{
    public static void MoveOverwrite(string source, string destination)
    {
#if NET48
        if (File.Exists(destination)) File.Delete(destination);
        File.Move(source, destination);
#else
        File.Move(source, destination, true);
#endif
    }

    public static string StableHash16(string value)
    {
#if NET48
        using SHA256 algorithm = SHA256.Create();
        byte[] hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
#else
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16];
#endif
    }
}

using System.Security.Cryptography;

namespace APC.Core.Seguridad;

/// <summary>
/// Hash de claves con PBKDF2-SHA256 (100.000 iteraciones, sal aleatoria de 16 bytes).
/// Formato guardado: v1.&lt;iteraciones&gt;.&lt;sal base64&gt;.&lt;hash base64&gt;
/// </summary>
public static class HashClave
{
    private const int Iteraciones = 100_000;
    private const int LargoSal = 16;
    private const int LargoHash = 32;

    public static string Crear(string clave)
    {
        var sal = RandomNumberGenerator.GetBytes(LargoSal);
        var hash = Rfc2898DeriveBytes.Pbkdf2(clave, sal, Iteraciones, HashAlgorithmName.SHA256, LargoHash);
        return $"v1.{Iteraciones}.{Convert.ToBase64String(sal)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verificar(string clave, string guardado)
    {
        if (string.IsNullOrEmpty(guardado)) return false;
        var partes = guardado.Split('.');
        if (partes.Length != 4 || partes[0] != "v1" || !int.TryParse(partes[1], out var iteraciones)) return false;
        try
        {
            var sal = Convert.FromBase64String(partes[2]);
            var esperado = Convert.FromBase64String(partes[3]);
            var hash = Rfc2898DeriveBytes.Pbkdf2(clave ?? "", sal, iteraciones, HashAlgorithmName.SHA256, esperado.Length);
            return CryptographicOperations.FixedTimeEquals(hash, esperado);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

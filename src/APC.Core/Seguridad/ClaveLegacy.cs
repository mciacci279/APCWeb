using System.Security.Cryptography;
using System.Text;

namespace APC.Core.Seguridad;

/// <summary>
/// Réplica exacta de APC.WinForms.Tools.Encriptador.Encriptar(clave, "", "", "SHA1", 2, "0000000000000000", 256),
/// que es como APC guarda Usuario.Pass. Se mantiene igual para que web y WinForms compartan usuarios.
/// OJO: es cifrado reversible con clave vacía, no un hash. Ver README (pendiente de seguridad).
/// </summary>
public static class ClaveLegacy
{
    private const string VectorInicial = "0000000000000000";

    public static string Cifrar(string textoPlano)
    {
        byte[] iv = Encoding.ASCII.GetBytes(VectorInicial);
        byte[] salt = Encoding.ASCII.GetBytes(string.Empty);
        byte[] datos = Encoding.UTF8.GetBytes(textoPlano);

#pragma warning disable SYSLIB0041
        using var derive = new PasswordDeriveBytes(string.Empty, salt, "SHA1", 2);
        byte[] key = derive.GetBytes(256 / 8);
#pragma warning restore SYSLIB0041

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor(key, iv);
        byte[] cifrado = enc.TransformFinalBlock(datos, 0, datos.Length);
        return Convert.ToBase64String(cifrado);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace APC.Core.Seguridad;

/// <summary>
/// Códigos de 6 dígitos de las apps autenticadoras (Google Authenticator, Microsoft Authenticator…).
/// Es el estándar TOTP (RFC 6238): HMAC-SHA1, pasos de 30 segundos.
/// </summary>
public static class Totp
{
    private const string Alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Secreto nuevo en Base32 (160 bits), para cargar en la app.</summary>
    public static string NuevoSecreto()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alfabeto[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alfabeto[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>Dirección que se muestra como código QR para agregar la cuenta en la app.</summary>
    public static string UriParaApp(string secreto, string usuario, string emisor = "APC Web") =>
        $"otpauth://totp/{Uri.EscapeDataString(emisor)}:{Uri.EscapeDataString(usuario)}" +
        $"?secret={secreto}&issuer={Uri.EscapeDataString(emisor)}&digits=6&period=30";

    /// <summary>Acepta el código actual y el de 30 segundos antes o después (por relojes desfasados).</summary>
    public static bool Verificar(string secreto, string codigo)
    {
        codigo = new string((codigo ?? "").Where(char.IsDigit).ToArray());
        if (codigo.Length != 6 || string.IsNullOrWhiteSpace(secreto)) return false;

        byte[] clave;
        try { clave = DeBase32(secreto); }
        catch (FormatException) { return false; }

        long paso = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        for (long d = -1; d <= 1; d++)
        {
            var esperado = Encoding.ASCII.GetBytes(Calcular(clave, paso + d));
            if (CryptographicOperations.FixedTimeEquals(esperado, Encoding.ASCII.GetBytes(codigo))) return true;
        }
        return false;
    }

    private static string Calcular(byte[] clave, long paso)
    {
        var contador = BitConverter.GetBytes(paso);
        if (BitConverter.IsLittleEndian) Array.Reverse(contador);
        using var hmac = new HMACSHA1(clave);
        var hash = hmac.ComputeHash(contador);
        int offset = hash[^1] & 0x0F;
        int binario = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binario % 1_000_000).ToString("000000");
    }

    private static byte[] DeBase32(string texto)
    {
        texto = texto.Trim().TrimEnd('=').ToUpperInvariant();
        var salida = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in texto)
        {
            int v = Alfabeto.IndexOf(c);
            if (v < 0) throw new FormatException("Secreto inválido.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                salida.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return salida.ToArray();
    }
}

using System.Security.Cryptography;
using System.Text;

namespace APC.Core.Seguridad;

/// <summary>
/// Links públicos a un comprobante, para mandarle al cliente por WhatsApp o email.
/// El link lleva una firma que solo este servidor puede generar: sin la firma correcta
/// no se puede ver el comprobante, y no se puede adivinar el de otra factura.
/// </summary>
public static class EnlaceComprobante
{
    private static readonly object Candado = new();
    private static byte[] _clave;

    /// <summary>La clave se crea una vez y queda guardada en la carpeta de datos del servidor.</summary>
    private static byte[] Clave(string carpetaDatos)
    {
        lock (Candado)
        {
            if (_clave != null) return _clave;
            Directory.CreateDirectory(carpetaDatos);
            var archivo = Path.Combine(carpetaDatos, "enlaces.key");
            if (File.Exists(archivo))
            {
                var guardada = File.ReadAllText(archivo).Trim();
                if (guardada.Length >= 40) return _clave = Convert.FromBase64String(guardada);
            }
            var nueva = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(archivo, Convert.ToBase64String(nueva));
            return _clave = nueva;
        }
    }

    public static string Firmar(string carpetaDatos, string codigoEmpresa, Guid comprobanteId, bool esNotaCredito)
    {
        var texto = $"{codigoEmpresa.Trim().ToLowerInvariant()}|{comprobanteId:N}|{(esNotaCredito ? 1 : 0)}";
        using var hmac = new HMACSHA256(Clave(carpetaDatos));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(texto));
        // 20 bytes alcanzan y dejan un link más corto; formato apto para direcciones web
        return Convert.ToBase64String(hash, 0, 20).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static bool Verificar(string carpetaDatos, string codigoEmpresa, Guid comprobanteId, bool esNotaCredito, string firma)
    {
        if (string.IsNullOrWhiteSpace(firma) || string.IsNullOrWhiteSpace(codigoEmpresa)) return false;
        var esperada = Firmar(carpetaDatos, codigoEmpresa, comprobanteId, esNotaCredito);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(esperada), Encoding.UTF8.GetBytes(firma.Trim()));
    }

    /// <summary>Ruta del link público: /ver/{empresa}/{id}?f={firma}[&amp;nc=1]</summary>
    public static string Ruta(string carpetaDatos, string codigoEmpresa, Guid comprobanteId, bool esNotaCredito) =>
        $"ver/{Uri.EscapeDataString(codigoEmpresa.Trim().ToLowerInvariant())}/{comprobanteId}?f={Firmar(carpetaDatos, codigoEmpresa, comprobanteId, esNotaCredito)}" +
        (esNotaCredito ? "&nc=1" : "");

    /// <summary>
    /// Link de WhatsApp con el mensaje ya escrito. Con teléfono abre el chat de ese número;
    /// sin teléfono, WhatsApp deja elegir el contacto.
    /// </summary>
    public static string WhatsApp(string telefono, string mensaje)
    {
        var numero = NumeroArgentino(telefono);
        return "https://wa.me/" + numero + "?text=" + Uri.EscapeDataString(mensaje ?? "");
    }

    /// <summary>
    /// Pasa un celular argentino al formato internacional de WhatsApp (549 + característica + número).
    /// Si ya viene con 54 adelante se respeta. Vacío si no hay número.
    /// </summary>
    public static string NumeroArgentino(string telefono)
    {
        var d = new string((telefono ?? "").Where(char.IsDigit).ToArray());
        if (d.Length < 8) return "";
        if (d.StartsWith("54")) return d.StartsWith("549") ? d : "549" + d[2..];
        if (d.StartsWith("0")) d = d[1..];
        return "549" + d;
    }
}

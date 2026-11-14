using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using APC.Core.Tenants;

namespace APC.Core.Servicios;

public class CertificadoInfo
{
    public bool Existe { get; set; }
    public string Sujeto { get; set; }
    public string Cuit { get; set; }
    public DateTime? Vence { get; set; }
    public bool ClaveCargada { get; set; }
    public bool ClaveCoincide { get; set; }
    public string Error { get; set; }
    /// <summary>Emitido por "Computadores Test" (homologación) o "Computadores" (producción).</summary>
    public bool EsHomologacion { get; set; }
    public bool Vencido => Vence < DateTime.Now;
    public bool VencePronto => Vence < DateTime.Now.AddDays(30);
}

/// <summary>
/// Guarda en el servidor el certificado y la clave de ARCA que CM Tech sube desde el panel,
/// en &lt;CarpetaCertificados&gt;\&lt;código&gt;\homo|prod\, y los valida.
/// </summary>
public class CertificadoService
{
    private const int TamanioMaximo = 32 * 1024;
    private static readonly Regex CodigoValido = new("^[a-z0-9][a-z0-9-]{1,29}$");
    private static readonly Regex CuitEnSujeto = new(@"CUIT\s*(\d{11})", RegexOptions.IgnoreCase);
    private readonly TenantRegistry _tenants;

    public CertificadoService(TenantRegistry tenants) => _tenants = tenants;

    public static int MaximoBytes => TamanioMaximo;

    public async Task<(string Ruta, string Error)> GuardarAsync(string codigo, string ambiente, bool esClave, Stream contenido)
    {
        codigo = codigo?.Trim().ToLowerInvariant();
        if (codigo == null || !CodigoValido.IsMatch(codigo)) return (null, "Escribí primero un código de empresa válido.");

        string texto;
        using (var ms = new MemoryStream())
        {
            await contenido.CopyToAsync(ms);
            if (ms.Length > TamanioMaximo) return (null, "El archivo es demasiado grande para ser un certificado.");
            texto = Encoding.UTF8.GetString(ms.ToArray()).Trim();
        }

        if (esClave)
        {
            if (!texto.Contains("PRIVATE KEY-----"))
                return (null, "Ese archivo no es una clave privada (.key en formato PEM).");
            try { using var rsa = RSA.Create(); rsa.ImportFromPem(texto); }
            catch (Exception) { return (null, "No se pudo leer la clave privada. Verificá que sea la .key sin contraseña."); }
        }
        else
        {
            if (!texto.Contains("BEGIN CERTIFICATE"))
                return (null, "Ese archivo no es un certificado (.crt en formato PEM).");
            try { using var c = X509Certificate2.CreateFromPem(texto); }
            catch (Exception) { return (null, "No se pudo leer el certificado."); }
        }

        var carpeta = Path.Combine(_tenants.CarpetaCertificados, codigo,
            string.Equals(ambiente, "PROD", StringComparison.OrdinalIgnoreCase) ? "prod" : "homo");
        Directory.CreateDirectory(carpeta);
        var ruta = Path.Combine(carpeta, esClave ? "clave.key" : "certificado.crt");
        await File.WriteAllTextAsync(ruta, texto + Environment.NewLine);
        return (ruta, null);
    }

    /// <summary>Lee el certificado guardado: sujeto, CUIT, vencimiento y si la clave le corresponde.</summary>
    public CertificadoInfo Leer(string certificadoPath, string clavePath)
    {
        var info = new CertificadoInfo();
        if (string.IsNullOrWhiteSpace(certificadoPath) || !File.Exists(certificadoPath)) return info;
        try
        {
            var certPem = File.ReadAllText(certificadoPath);
            using var cert = X509Certificate2.CreateFromPem(certPem);
            info.Existe = true;
            info.Sujeto = cert.GetNameInfo(X509NameType.SimpleName, false);
            info.Vence = cert.NotAfter;
            info.EsHomologacion = cert.Issuer.Contains("Test", StringComparison.OrdinalIgnoreCase);
            var m = CuitEnSujeto.Match(cert.Subject);
            if (m.Success) info.Cuit = m.Groups[1].Value;

            if (!string.IsNullOrWhiteSpace(clavePath) && File.Exists(clavePath))
            {
                info.ClaveCargada = true;
                try
                {
                    using var par = X509Certificate2.CreateFromPem(certPem, File.ReadAllText(clavePath));
                    info.ClaveCoincide = par.HasPrivateKey;
                }
                catch (CryptographicException)
                {
                    info.ClaveCoincide = false;
                }
            }
        }
        catch (Exception ex)
        {
            info.Error = "No se pudo leer el certificado: " + ex.Message;
        }
        return info;
    }
}

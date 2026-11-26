using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;

namespace APC.Web.Servicios;

/// <summary>Estado de los servicios de ARCA en un ambiente (producción u homologación).</summary>
public class EstadoArca
{
    /// <summary>null = todavía no se consultó.</summary>
    public bool? Ok { get; init; }
    public string AppServer { get; init; }
    public string DbServer { get; init; }
    public string AuthServer { get; init; }
    public string Error { get; init; }
    public DateTime? Consultado { get; init; }
}

/// <summary>
/// Mira cada <see cref="Intervalo"/> si ARCA responde, con FEDummy de la factura electrónica (WSFE): es el
/// método de "ping" de ARCA, no pide certificado ni ticket. Se consulta UNA vez por ambiente para todo el
/// servidor (no por usuario) y las pantallas leen el último resultado guardado, así que no cuesta nada.
/// </summary>
public class ArcaEstadoService : BackgroundService
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(60);
    private const string Prod = "https://servicios1.afip.gov.ar/wsfev1/service.asmx";
    private const string Homo = "https://wswhomo.afip.gov.ar/wsfev1/service.asmx";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private volatile EstadoArca _prod = new();
    private volatile EstadoArca _homo = new();

    /// <summary>Último estado conocido del ambiente de la empresa.</summary>
    public EstadoArca Estado(bool produccion) => produccion ? _prod : _homo;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var p = Consultar(Prod, ct);
            var h = Consultar(Homo, ct);
            _prod = await p;
            _homo = await h;
            try { await Task.Delay(Intervalo, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private static async Task<EstadoArca> Consultar(string url, CancellationToken ct)
    {
        try
        {
            const string sobre =
                "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                "<soap:Body><FEDummy xmlns=\"http://ar.gov.afip.dif.FEV1/\"/></soap:Body></soap:Envelope>";
            using var contenido = new StringContent(sobre, Encoding.UTF8, "text/xml");
            contenido.Headers.Add("SOAPAction", "\"http://ar.gov.afip.dif.FEV1/FEDummy\"");
            using var r = await Http.PostAsync(url, contenido, ct);
            var xml = await r.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);
            string V(string n) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value?.Trim();
            var app = V("AppServer"); var db = V("DbServer"); var auth = V("AuthServer");
            bool ok = string.Equals(app, "OK", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(db, "OK", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(auth, "OK", StringComparison.OrdinalIgnoreCase);
            return new EstadoArca { Ok = ok, AppServer = app, DbServer = db, AuthServer = auth, Consultado = DateTime.Now,
                                    Error = ok ? null : (app == null ? "ARCA respondió sin estado (HTTP " + (int)r.StatusCode + ")" : null) };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new EstadoArca { Ok = false, Error = ex.Message, Consultado = DateTime.Now };
        }
    }
}

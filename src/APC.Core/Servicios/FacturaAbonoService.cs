using System.Text;
using System.Text.Json;
using APC.Arca;
using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Tenants;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Emite la factura del abono de CM Tech. Cada facturador es monotributista, así que emite
/// Factura C (tipo 11) con su propio certificado y punto de venta de ARCA.
/// Los datos del receptor se toman de la base de la empresa a la que se le cobra.
/// </summary>
public class FacturaAbonoService
{
    private const string UrlQrArca = "https://www.afip.gob.ar/fe/qr/?p=";
    private const int FacturaC = 11;

    private readonly ArcaService _arca;
    private readonly TenantRegistry _tenants;
    private readonly CatalogoService _catalogo;
    private readonly CobranzaService _cobranzas;

    public FacturaAbonoService(ArcaService arca, TenantRegistry tenants, CatalogoService catalogo, CobranzaService cobranzas)
    {
        _arca = arca;
        _tenants = tenants;
        _catalogo = catalogo;
        _cobranzas = cobranzas;
    }

    private string Central => _tenants.Central;

    /// <summary>Pide el CAE a ARCA y guarda el comprobante en el cobro. Devuelve el error o null.</summary>
    public async Task<string> EmitirAsync(Guid cobroId, Guid usuarioId)
    {
        using var cn = await Db.AbrirAsync(Central);
        var cobro = await cn.QueryFirstOrDefaultAsync<Cobro>(
            @"SELECT c.Id, c.EmpresaId, c.Periodo, c.Concepto, c.Monto, c.FacturadorId, c.Estado, c.CAE,
                     e.Codigo AS EmpresaCodigo, e.Nombre AS EmpresaNombre
              FROM dbo.Cobro c JOIN dbo.Empresa e ON e.Id = c.EmpresaId
              WHERE c.Id = @cobroId", new { cobroId });

        if (cobro == null) return "El cobro no existe.";
        if (cobro.Facturado) return "Ese cobro ya tiene factura emitida.";
        if (cobro.Estado == EstadoCobro.Anulado) return "El cobro está anulado.";
        if (cobro.Monto <= 0) return "El importe tiene que ser mayor que cero.";
        if (cobro.FacturadorId == null) return "Elegí quién emite la factura.";

        var facturadores = await _cobranzas.FacturadoresAsync();
        var f = facturadores.FirstOrDefault(x => x.Id == cobro.FacturadorId);
        if (f == null) return "No se encontró el facturador.";
        if (!f.PuedeFacturar)
            return $"A {f.Nombre} le faltan datos para facturar: CUIT, punto de venta y el certificado de ARCA.";

        // ---- Receptor: los datos fiscales salen de la base de la empresa a la que se le cobra ----
        var tenant = _tenants.BuscarCualquiera(cobro.EmpresaCodigo);
        string cuitReceptor = null, nombreReceptor = cobro.EmpresaNombre, domicilioReceptor = null, condicionReceptor = null;
        if (tenant != null)
        {
            try
            {
                var emp = await _catalogo.EmpresaAsync(tenant.ConnectionString);
                if (emp != null)
                {
                    cuitReceptor = emp.Cuit;
                    nombreReceptor = emp.Nombre;
                    domicilioReceptor = emp.Domicilio;
                    condicionReceptor = emp.CondicionIva;
                }
            }
            catch (Exception)
            {
                // Si su base no responde, se factura igual con lo que hay en el panel.
            }
        }

        var doc = new string((cuitReceptor ?? "").Where(char.IsDigit).ToArray());
        if (doc.Length != 11)
            return "No se pudo obtener el CUIT de la empresa. Cargalo en los datos fiscales de su base, o revisá la conexión.";

        var solicitud = new SolicitudCae
        {
            TipoComprobante = FacturaC,
            TipoDocReceptor = 80,                      // 80 = CUIT
            NroDocReceptor = long.Parse(doc),
            CondicionIvaReceptorId = CondicionArca(condicionReceptor),
            Fecha = DateTime.Today,
            ImporteTotal = cobro.Monto,
            ImporteNeto = cobro.Monto                  // En la Factura C no se discrimina IVA
        };

        var cfg = new ArcaConfig
        {
            Cuit = f.CuitNumero,
            PuntoVenta = f.PuntoVenta.Value,
            CertificadoPath = f.CertificadoPath,
            ClavePath = f.ClavePath,
            UrlWsaa = f.UrlWsaa,
            UrlWsfe = f.UrlWsfe,
            Concepto = 2,                              // 2 = Servicios
            Moneda = "PES",
            CarpetaCacheTA = Path.Combine(_tenants.CarpetaDatos, "ta", f.CuitNumero + "_" + f.Ambiente)
        };

        RespuestaCae r;
        try
        {
            r = await Task.Run(() => _arca.SolicitarCae(cfg, solicitud));
        }
        catch (Exception ex)
        {
            return "No se pudo conectar con ARCA: " + ex.GetBaseException().Message;
        }
        if (!r.Aprobado) return "ARCA rechazó la factura: " + r.Mensaje;

        var qrJson = JsonSerializer.Serialize(new
        {
            ver = 1,
            fecha = solicitud.Fecha.ToString("yyyy-MM-dd"),
            cuit = f.CuitNumero,
            ptoVta = f.PuntoVenta.Value,
            tipoCmp = FacturaC,
            nroCmp = r.Numero,
            importe = cobro.Monto,
            moneda = "PES",
            ctz = 1,
            tipoDocRec = 80,
            nroDocRec = long.Parse(doc),
            tipoCodAut = "E",
            codAut = long.Parse(r.Cae)
        });

        await cn.ExecuteAsync(
            @"UPDATE dbo.Cobro SET
                TipoComprobanteAFIP = @tipo, PuntoVenta = @pv, Numero = @nro, CAE = @cae,
                CAEVencimiento = @vto, QRURLAFIP = @qr, FechaEmision = GETDATE(), EmitidoPor = @usuarioId,
                ComprobanteNro = @comprobante,
                ReceptorCuit = @cuit, ReceptorNombre = @nombre, ReceptorDomicilio = @domicilio, ReceptorCondicionIva = @condicion
              WHERE Id = @cobroId",
            new
            {
                cobroId, usuarioId, tipo = FacturaC, pv = f.PuntoVenta.Value, nro = (int)r.Numero,
                cae = r.Cae, vto = r.CaeVencimiento, qr = UrlQrArca + Convert.ToBase64String(Encoding.UTF8.GetBytes(qrJson)),
                comprobante = $"C {f.PuntoVenta.Value:00000}-{r.Numero:00000000}",
                cuit = doc, nombre = Recortar(nombreReceptor, 150), domicilio = Recortar(domicilioReceptor, 150),
                condicion = Recortar(condicionReceptor, 60)
            });

        return null;
    }

    /// <summary>Arma el comprobante para imprimirlo con la misma hoja A4 que usa el sistema.</summary>
    public async Task<(ComprobanteImpresion Comprobante, EmpresaDatos Emisor)> ParaImprimirAsync(Guid cobroId)
    {
        using var cn = await Db.AbrirAsync(Central);
        var c = await cn.QueryFirstOrDefaultAsync<Cobro>(
            @"SELECT c.Id, c.EmpresaId, c.Periodo, c.Concepto, c.Monto, c.FacturadorId, c.Estado,
                     c.TipoComprobanteAFIP, c.PuntoVenta, c.Numero, c.CAE, c.CAEVencimiento, c.QRURLAFIP,
                     c.FechaEmision, c.ReceptorCuit, c.ReceptorNombre, c.ReceptorDomicilio, c.ReceptorCondicionIva,
                     c.MedioPago, e.Nombre AS EmpresaNombre, e.NombreFantasia AS EmpresaFantasia
              FROM dbo.Cobro c JOIN dbo.Empresa e ON e.Id = c.EmpresaId
              WHERE c.Id = @cobroId", new { cobroId });
        if (c == null || !c.Facturado) return (null, null);

        var f = (await _cobranzas.FacturadoresAsync()).FirstOrDefault(x => x.Id == c.FacturadorId);
        if (f == null) return (null, null);

        var emisor = new EmpresaDatos
        {
            Nombre = f.Nombre,
            Cuit = f.Cuit,
            Domicilio = f.Domicilio,
            CP = f.Localidad,
            IngresosBrutos = f.IngresosBrutos,
            FechaInicioActividades = f.FechaInicioActividades,
            CondicionIva = "RESPONSABLE MONOTRIBUTO",
            EsMonotributo = true
        };

        var comprobante = new ComprobanteImpresion
        {
            Id = c.Id,
            Fecha = c.FechaEmision ?? DateTime.Now,
            Tipo = "C",
            Terminal = c.PuntoVenta ?? 0,
            Numero = c.Numero,
            TipoComprobanteAFIP = c.TipoComprobanteAFIP,
            MontoTotal = c.Monto,
            MontoIva = 0,
            ClienteNombre = c.ReceptorNombre,
            ClienteCUIT = c.ReceptorCuit,
            ClienteDomicilio = c.ReceptorDomicilio,
            ClienteCondicionIva = c.ReceptorCondicionIva,
            CAE = c.CAE,
            CAEVencimiento = c.CAEVencimiento,
            QRURLAFIP = c.QRURLAFIP,
            FormaPago = string.IsNullOrWhiteSpace(c.MedioPago) ? "Transferencia" : c.MedioPago,
            Usuario = f.Nombre,
            Impresa = true,
            Renglones = new List<RenglonImpresion>
            {
                new()
                {
                    Producto = string.IsNullOrWhiteSpace(c.Concepto) ? "Abono APC Web · " + Cobro.TextoDePeriodo(c.Periodo)
                             : DescripcionConFechas.Reemplazar(c.Concepto, c.FechaEmision ?? DateTime.Now),
                    CantidadRenglon = 1,
                    PU = c.Monto,
                    MontoRenglon = c.Monto
                }
            }
        };
        return (comprobante, emisor);
    }

    /// <summary>Condición del receptor frente al IVA, según lo que diga su propia base.</summary>
    private static int CondicionArca(string condicion)
    {
        var t = (condicion ?? "").ToUpperInvariant();
        if (t.Contains("INSCRIPTO")) return WsfeCondicionIvaReceptor.IVAResponsableInscripto;
        if (t.Contains("MONOTRIBUT")) return WsfeCondicionIvaReceptor.ResponsableMonotributo;
        if (t.Contains("EXENT")) return WsfeCondicionIvaReceptor.IVASujetoExento;
        return WsfeCondicionIvaReceptor.ConsumidorFinal;
    }

    private static string Recortar(string texto, int largo)
    {
        texto = texto?.Trim();
        if (string.IsNullOrEmpty(texto)) return null;
        return texto.Length <= largo ? texto : texto[..largo];
    }
}

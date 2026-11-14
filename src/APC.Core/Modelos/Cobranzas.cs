namespace APC.Core.Modelos;

/// <summary>Quién emite la factura del abono: cada integrante de CM Tech, con sus datos de cobro.</summary>
public class Facturador
{
    public Guid Id { get; set; }
    public string Nombre { get; set; }
    public string Cuit { get; set; }
    public string Alias { get; set; }
    public string Cbu { get; set; }
    public string Banco { get; set; }
    public bool Activo { get; set; } = true;

    // ---- Datos para emitir la Factura C por ARCA ----
    public int? PuntoVenta { get; set; }
    /// <summary>"HOMO" (pruebas) o "PROD".</summary>
    public string Ambiente { get; set; } = "HOMO";
    public string CertificadoPath { get; set; }
    public string ClavePath { get; set; }
    public string Domicilio { get; set; }
    public string Localidad { get; set; }
    public string IngresosBrutos { get; set; }
    public DateTime? FechaInicioActividades { get; set; }

    public bool EsNuevo => Id == Guid.Empty;
    public long CuitNumero => long.TryParse((Cuit ?? "").Replace("-", "").Replace(" ", "").Trim(), out var c) ? c : 0;
    public bool EsProduccion => string.Equals(Ambiente?.Trim(), "PROD", StringComparison.OrdinalIgnoreCase);

    public string UrlWsaa => EsProduccion
        ? "https://wsaa.afip.gov.ar/ws/services/LoginCms"
        : "https://wsaahomo.afip.gov.ar/ws/services/LoginCms";

    public string UrlWsfe => EsProduccion
        ? "https://servicios1.afip.gov.ar/wsfev1/service.asmx"
        : "https://wswhomo.afip.gov.ar/wsfev1/service.asmx";

    /// <summary>Puede emitir por ARCA: tiene CUIT, punto de venta y certificado.</summary>
    public bool PuedeFacturar =>
        CuitNumero > 0 && PuntoVenta > 0 &&
        !string.IsNullOrWhiteSpace(CertificadoPath) && !string.IsNullOrWhiteSpace(ClavePath);

    /// <summary>Código con el que se guardan sus certificados en el servidor.</summary>
    public string CodigoCertificado => "facturador-" + (CuitNumero > 0 ? CuitNumero.ToString() : Id.ToString("N")[..8]);

    /// <summary>Línea con los datos para transferir, para el mensaje al cliente.</summary>
    public string DatosParaTransferir
    {
        get
        {
            var partes = new List<string>();
            if (!string.IsNullOrWhiteSpace(Alias)) partes.Add("Alias: " + Alias.Trim());
            else if (!string.IsNullOrWhiteSpace(Cbu)) partes.Add("CBU: " + Cbu.Trim());
            if (!string.IsNullOrWhiteSpace(Nombre)) partes.Add("a nombre de " + Nombre.Trim());
            return string.Join(" ", partes);
        }
    }
}

public static class EstadoCobro
{
    public const string Pendiente = "PENDIENTE";
    public const string Enviado = "ENVIADO";
    public const string Abonado = "ABONADO";
    public const string Anulado = "ANULADO";
}

/// <summary>El abono de una empresa en un mes.</summary>
public class Cobro
{
    public Guid Id { get; set; }
    public Guid EmpresaId { get; set; }
    /// <summary>"aaaa-mm".</summary>
    public string Periodo { get; set; }
    public string Concepto { get; set; }
    public decimal Monto { get; set; }
    public Guid? FacturadorId { get; set; }
    public string ComprobanteNro { get; set; }
    public string Estado { get; set; } = EstadoCobro.Pendiente;
    public DateTime FechaAlta { get; set; }
    public DateTime? FechaEnvio { get; set; }
    public DateTime? FechaPago { get; set; }
    public string MedioPago { get; set; }
    public string Observaciones { get; set; }

    // ---- Factura C emitida por ARCA ----
    public int? TipoComprobanteAFIP { get; set; }
    public int? PuntoVenta { get; set; }
    public int? Numero { get; set; }
    public string CAE { get; set; }
    public string CAEVencimiento { get; set; }
    public string QRURLAFIP { get; set; }
    public DateTime? FechaEmision { get; set; }
    public string ReceptorCuit { get; set; }
    public string ReceptorNombre { get; set; }
    public string ReceptorDomicilio { get; set; }
    public string ReceptorCondicionIva { get; set; }

    public bool Facturado => !string.IsNullOrEmpty(CAE);
    public string NumeroFactura => Facturado ? $"{PuntoVenta ?? 0:00000}-{Numero ?? 0:00000000}" : ComprobanteNro;

    // Datos que se leen junto con el cobro, para la pantalla
    public string EmpresaCodigo { get; set; }
    public string EmpresaNombre { get; set; }
    public string EmpresaFantasia { get; set; }
    public string ContactoNombre { get; set; }
    public string ContactoTelefono { get; set; }
    public string ContactoEmail { get; set; }
    public string Facturador { get; set; }
    public string FacturadorDatos { get; set; }
    public string CreadoPorNombre { get; set; }
    public string EnviadoPorNombre { get; set; }
    public string CobradoPorNombre { get; set; }
    public int Avisos { get; set; }

    public bool Abonado => Estado == EstadoCobro.Abonado;
    public bool Anulado => Estado == EstadoCobro.Anulado;
    public string Comercio => string.IsNullOrWhiteSpace(EmpresaFantasia) ? EmpresaNombre : EmpresaFantasia;

    /// <summary>"2026-11" → "noviembre de 2026".</summary>
    public string PeriodoTexto => TextoDePeriodo(Periodo);

    public static string TextoDePeriodo(string periodo)
    {
        if (!DateTime.TryParse(periodo + "-01", out var f)) return periodo;
        var ar = new System.Globalization.CultureInfo("es-AR");
        return f.ToString("MMMM 'de' yyyy", ar);
    }
}

public class ResumenCobranzas
{
    public string Periodo { get; set; }
    public int Cantidad { get; set; }
    public decimal Total { get; set; }
    public int CantidadAbonada { get; set; }
    public decimal Cobrado { get; set; }
    public decimal Pendiente => Total - Cobrado;
    public int SinAvisar { get; set; }
}

public class EnvioCobro
{
    public DateTime Fecha { get; set; }
    public string Medio { get; set; }
    public string Destino { get; set; }
    public string Usuario { get; set; }
    public string Nota { get; set; }
}

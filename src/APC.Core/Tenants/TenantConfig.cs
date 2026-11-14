namespace APC.Core.Tenants;

/// <summary>
/// Una empresa cliente. Se guarda en la base central (APCWeb_Central.dbo.Empresa)
/// y la administra CM Tech desde el panel /admin/empresas.
/// </summary>
public class TenantConfig
{
    public Guid Id { get; set; }
    /// <summary>Código corto que el usuario escribe al ingresar, ej. "mena".</summary>
    public string Codigo { get; set; }
    /// <summary>Razón social.</summary>
    public string Nombre { get; set; }
    /// <summary>Nombre de fantasía del negocio (opcional).</summary>
    public string NombreFantasia { get; set; }
    /// <summary>Conexión a la base de datos propia de la empresa.</summary>
    public string ConnectionString { get; set; }

    public int PuntoVenta { get; set; }
    /// <summary>"HOMO" (homologación) o "PROD".</summary>
    public string Ambiente { get; set; } = "HOMO";
    public string CertificadoPath { get; set; }
    public string ClavePath { get; set; }
    /// <summary>1 = Productos, 2 = Servicios, 3 = Productos y servicios.</summary>
    public int Concepto { get; set; } = 1;
    public string Moneda { get; set; } = "PES";

    /// <summary>Texto al pie del ticket. Vacío = sin leyenda.</summary>
    public string LeyendaTicket { get; set; } = "Muchas gracias por su compra!!!";

    public bool Activa { get; set; } = true;
    public string MotivoSuspension { get; set; }
    public DateTime FechaAlta { get; set; }
    /// <summary>Solo se completa en el listado del panel.</summary>
    public int CantidadUsuarios { get; set; }
    /// <summary>Solo se completa en el listado del panel.</summary>
    public bool TieneLogo { get; set; }

    public bool EsProduccion => string.Equals(Ambiente?.Trim(), "PROD", StringComparison.OrdinalIgnoreCase);

    public string UrlWsaa => EsProduccion
        ? "https://wsaa.afip.gov.ar/ws/services/LoginCms"
        : "https://wsaahomo.afip.gov.ar/ws/services/LoginCms";

    public string UrlWsfe => EsProduccion
        ? "https://servicios1.afip.gov.ar/wsfev1/service.asmx"
        : "https://wswhomo.afip.gov.ar/wsfev1/service.asmx";
}

public class TenantOptions
{
    /// <summary>Carpeta base para el caché de tickets de ARCA (una subcarpeta por CUIT).</summary>
    public string CarpetaDatos { get; set; } = "App_Data";
    /// <summary>Carpeta donde el panel guarda los certificados de ARCA (una subcarpeta por empresa).</summary>
    public string CarpetaCertificados { get; set; } = "C:\\APCWeb\\Certificados";
    /// <summary>
    /// Dirección pública del sitio (ej. https://apc.cm-tech.com.ar/) para armar los links de los emails.
    /// Si está vacía se usa la del pedido (solo recomendable en desarrollo).
    /// </summary>
    public string UrlPublica { get; set; }
    /// <summary>Conexión a la base central APCWeb_Central (ConnectionStrings:Central).</summary>
    public string CentralConnectionString { get; set; }
}

namespace APC.Core.Modelos;

/// <summary>Cliente en la lista y en el buscador de la pantalla de facturación (tabla dbo.Cliente).</summary>
public class ClienteItem
{
    public Guid Id { get; set; }
    public string Apellido { get; set; }
    public string Nombre { get; set; }
    public string NroDocumento { get; set; }
    public string TipoDocumento { get; set; }
    public string Domicilio { get; set; }
    public string Localidad { get; set; }
    public string CondicionIva { get; set; }
    public string Telefono1 { get; set; }
    public string Email { get; set; }

    /// <summary>Razón social, o apellido y nombre.</summary>
    public string NombreCompleto =>
        string.Join(" ", new[] { Apellido, Nombre }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

    public string Documento =>
        string.IsNullOrWhiteSpace(NroDocumento) ? "" :
        string.IsNullOrWhiteSpace(TipoDocumento) ? NroDocumento : $"{TipoDocumento} {NroDocumento}";
}

/// <summary>Datos completos del cliente para el alta y la modificación.</summary>
public class ClienteDetalle
{
    public Guid Id { get; set; }
    public string Apellido { get; set; }
    public string Nombre { get; set; }
    public Guid? TipoDocumentoId { get; set; }
    public string NroDocumento { get; set; }
    public Guid? IvaId { get; set; }
    public string Domicilio { get; set; }
    public string NroCalle { get; set; }
    public string Barrio { get; set; }
    public string Localidad { get; set; }
    public string Cp { get; set; }
    public string IngresosBrutos { get; set; }
    public string Telefono1 { get; set; }
    public string Telefono2 { get; set; }
    public string Email { get; set; }

    public bool EsNuevo => Id == Guid.Empty;
}

/// <summary>Una fila de dbo.TipoDocumento o dbo.Iva, para los desplegables.</summary>
public class OpcionItem
{
    public Guid Id { get; set; }
    public string Nombre { get; set; }
}

namespace APC.Core.Modelos;

/// <summary>Presupuesto (tablas dbo.Presupuesto / dbo.DetallePresupuesto de APC). No es fiscal, no mueve caja ni stock.</summary>
public class PresupuestoListado
{
    public Guid Id { get; set; }
    public DateTime Fecha { get; set; }
    public int Terminal { get; set; }
    public int Numero { get; set; }
    public decimal MontoTotal { get; set; }
    public string Observaciones { get; set; }
    public string FormaPago { get; set; }
    public string Usuario { get; set; }
    public bool Cancelado { get; set; }
    public bool Facturado { get; set; }
    public string ClienteNombre => PresupuestoCliente.Leer(Observaciones).Nombre;
    public string NumeroCompleto => $"{Terminal:00000}-{Numero:00000000}";
}

public class PresupuestoRenglon
{
    public Guid ProductoId { get; set; }
    public string Codigo { get; set; }
    public string Producto { get; set; }
    public decimal Pu { get; set; }
    public decimal CantidadRenglon { get; set; }
    public decimal DescuentoRenglon { get; set; }
    public decimal MontoRenglon { get; set; }
}

public class PresupuestoImpresion : PresupuestoListado
{
    public Guid? FormaPagoId { get; set; }
    public decimal MontoDescuento { get; set; }
    public decimal MontoRecargo { get; set; }
    public decimal DescuentoTotalPorc { get; set; }
    public decimal RecargoTotalPorc { get; set; }
    public List<PresupuestoRenglon> Renglones { get; set; } = new();
    public PresupuestoCliente Cliente => PresupuestoCliente.Leer(Observaciones);
}

/// <summary>
/// La tabla Presupuesto no tiene columnas para el cliente ocasional: se guardan al principio de
/// Observaciones, una línea por dato ("Cliente: …"), y el resto son las observaciones libres.
/// </summary>
public class PresupuestoCliente
{
    public string Nombre { get; set; }
    public string Documento { get; set; }
    public string Domicilio { get; set; }
    public string Localidad { get; set; }
    public string Observaciones { get; set; }

    private const string PNombre = "Cliente: ", PDoc = "CUIT/DNI: ", PDom = "Domicilio: ", PLoc = "Localidad: ";

    public static string Armar(NuevaFactura f)
    {
        var l = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.ClienteNombre)) l.Add(PNombre + f.ClienteNombre.Trim());
        if (!string.IsNullOrWhiteSpace(f.ClienteDocumento)) l.Add(PDoc + f.ClienteDocumento.Trim());
        if (!string.IsNullOrWhiteSpace(f.ClienteDomicilio)) l.Add(PDom + f.ClienteDomicilio.Trim());
        if (!string.IsNullOrWhiteSpace(f.ClienteLocalidad)) l.Add(PLoc + f.ClienteLocalidad.Trim());
        if (!string.IsNullOrWhiteSpace(f.Observaciones)) l.Add(f.Observaciones.Trim());
        return string.Join("\n", l);
    }

    public static PresupuestoCliente Leer(string obs)
    {
        var c = new PresupuestoCliente();
        var resto = new List<string>();
        foreach (var linea in (obs ?? "").Replace("\r", "").Split('\n'))
        {
            if (linea.StartsWith(PNombre)) c.Nombre = linea[PNombre.Length..];
            else if (linea.StartsWith(PDoc)) c.Documento = linea[PDoc.Length..];
            else if (linea.StartsWith(PDom)) c.Domicilio = linea[PDom.Length..];
            else if (linea.StartsWith(PLoc)) c.Localidad = linea[PLoc.Length..];
            else if (linea.Trim().Length > 0) resto.Add(linea);
        }
        c.Nombre ??= "Consumidor final";
        c.Observaciones = string.Join("\n", resto);
        return c;
    }
}

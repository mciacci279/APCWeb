namespace APC.Core.Modelos;

/// <summary>Una sucursal del comercio: tiene su punto de venta de ARCA y su propio stock.</summary>
public class Sucursal
{
    public Guid Id { get; set; }
    public string Nombre { get; set; }
    public int PuntoVenta { get; set; }
    public string Domicilio { get; set; }
    public string Localidad { get; set; }
    public string Telefono { get; set; }
    /// <summary>Leyenda propia al pie del ticket. Vacía = se usa la de la empresa.</summary>
    public string LeyendaTicket { get; set; }
    public bool Activa { get; set; } = true;
    /// <summary>Si hay que abrir la caja para poder facturar en esta sucursal.
    /// Un dueño que factura solo no la necesita; con empleados conviene dejarla en true.</summary>
    public bool ExigeCaja { get; set; } = true;
    public DateTime FechaAlta { get; set; }

    /// <summary>Cantidad de usuarios asignados (solo se completa en el listado).</summary>
    public int CantidadUsuarios { get; set; }

    public bool EsNueva => Id == Guid.Empty;
    public string Etiqueta => $"{Nombre} · Pto. Vta. {PuntoVenta:0000}";
}

/// <summary>Stock de un producto en una sucursal.</summary>
public class StockEnSucursal
{
    public Guid SucursalId { get; set; }
    public string Sucursal { get; set; }
    public int PuntoVenta { get; set; }
    public decimal Stock { get; set; }
    public decimal? StockMinimo { get; set; }
    public bool BajoMinimo => StockMinimo.HasValue && Stock <= StockMinimo.Value;
}

public class MovimientoStockItem
{
    public DateTime Fecha { get; set; }
    public string Sucursal { get; set; }
    public string Producto { get; set; }
    public string Codigo { get; set; }
    public decimal Cantidad { get; set; }
    public string Tipo { get; set; }
    public string Usuario { get; set; }
    public string Referencia { get; set; }
    public string Observaciones { get; set; }
}

public static class TipoMovimientoStock
{
    public const string Ajuste = "AJUSTE";
    public const string Alta = "ALTA";
    public const string TransferenciaSalida = "TRANSF. SALIDA";
    public const string TransferenciaEntrada = "TRANSF. ENTRADA";
}

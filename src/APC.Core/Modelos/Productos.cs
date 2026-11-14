namespace APC.Core.Modelos;

public class ProductoDetalle
{
    public Guid Id { get; set; }
    public string Codigo { get; set; }
    public string Nombre { get; set; }
    public decimal Stock { get; set; }
    public decimal PrecioCosto { get; set; }
    public decimal PrecioVenta1 { get; set; }
    public decimal? PrecioVenta2 { get; set; }
    public decimal? PrecioVenta3 { get; set; }
    public decimal StockMinimo { get; set; }
    public decimal StockMaximo { get; set; }
    public Guid? RubroId { get; set; }
    public string Rubro { get; set; }
    public bool IvaDiferenciado { get; set; }
    public bool IvaExento { get; set; }
    public bool OmitirControlStock { get; set; }
    public bool Activo { get; set; } = true;
}

public class RubroItem
{
    public Guid Id { get; set; }
    public string Nombre { get; set; }
}

public class CambioProductoHistorial
{
    public DateTime Fecha { get; set; }
    public decimal Cantidad { get; set; }
    public decimal? PU { get; set; }
    public decimal? PrecioVenta1 { get; set; }
    public decimal? PrecioVenta2 { get; set; }
    public decimal? PrecioVenta3 { get; set; }
    public string Observaciones { get; set; }
    public string Usuario { get; set; }
    public string Producto { get; set; }
    public string Codigo { get; set; }
}

/// <summary>Un cambio puntual de stock y/o precios de un producto (pantalla "Cambio de stock y precios").</summary>
public class CambioProducto
{
    /// <summary>Unidades que entran (+) o salen (−) del stock. 0 si solo cambian precios.</summary>
    public decimal CantidadStock { get; set; }
    public decimal PrecioCosto { get; set; }
    public decimal PrecioVenta1 { get; set; }
    public decimal? PrecioVenta2 { get; set; }
    public decimal? PrecioVenta3 { get; set; }
    public string Observaciones { get; set; }
}

public enum TipoAumento { Porcentaje, Pesos }

public class AumentoMasivo
{
    /// <summary>null = todos los productos activos.</summary>
    public Guid? RubroId { get; set; }
    public TipoAumento Tipo { get; set; } = TipoAumento.Porcentaje;
    /// <summary>Porcentaje o monto en pesos. Puede ser negativo (baja de precios).</summary>
    public decimal Valor { get; set; }
    public bool IncluirCosto { get; set; } = true;
    public string Observaciones { get; set; }
}

public class PorcentajesListas
{
    public decimal Lista1 { get; set; }
    public decimal Lista2 { get; set; }
    public decimal Lista3 { get; set; }
    public string Titulo1 { get; set; } = "Precio 1";
    public string Titulo2 { get; set; } = "Precio 2";
    public string Titulo3 { get; set; } = "Precio 3";
}

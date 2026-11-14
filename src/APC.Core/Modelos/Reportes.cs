namespace APC.Core.Modelos;

/// <summary>Totales del período para el encabezado de la pantalla de reportes.</summary>
public class ResumenVentas
{
    public int CantidadFacturas { get; set; }
    public decimal TotalFacturas { get; set; }
    public int CantidadNotasPedido { get; set; }
    public decimal TotalNotasPedido { get; set; }
    public int CantidadNotasCredito { get; set; }
    public decimal TotalNotasCredito { get; set; }
    public decimal IvaFacturas { get; set; }
    public decimal IvaNotasCredito { get; set; }

    /// <summary>Facturado menos las notas de crédito.</summary>
    public decimal VentasNetas => TotalFacturas - TotalNotasCredito;
    public decimal IvaNeto => IvaFacturas - IvaNotasCredito;
}

/// <summary>Una fila de cualquiera de los cuadros (por día, forma de pago, usuario…).</summary>
public class FilaReporte
{
    public string Etiqueta { get; set; }
    public string Detalle { get; set; }
    public int Cantidad { get; set; }
    public decimal Total { get; set; }
}

public class FilaProducto
{
    public string Codigo { get; set; }
    public string Producto { get; set; }
    public decimal Cantidad { get; set; }
    public decimal Total { get; set; }
}

/// <summary>Una línea del Libro IVA Ventas. En las notas de crédito los importes van en negativo.</summary>
public class FilaLibroIva
{
    public DateTime Fecha { get; set; }
    public string Comprobante { get; set; }
    public int? TipoComprobanteAFIP { get; set; }
    public string Letra { get; set; }
    public int Terminal { get; set; }
    public int? Numero { get; set; }
    public string ClienteNombre { get; set; }
    public string ClienteCUIT { get; set; }
    public string CondicionIva { get; set; }
    public string CAE { get; set; }

    public decimal MontoTotal { get; set; }
    public decimal MontoIva { get; set; }
    public decimal? MontoNoGravado { get; set; }
    public decimal? MontoExento { get; set; }
    public decimal? MontoIvaTipoAfip4 { get; set; }
    public decimal? MontoIvaTipoAfip5 { get; set; }
    public decimal? MontoIvaTipoAfip6 { get; set; }
    public decimal? Iva { get; set; }

    public string NumeroCompleto => $"{Terminal:00000}-{(Numero ?? 0):00000000}";
    public decimal NoGravado => MontoNoGravado ?? 0;
    public decimal Exento => MontoExento ?? 0;
    public decimal NetoGravado => MontoTotal - MontoIva - NoGravado - Exento;

    /// <summary>IVA 10,5 %. Si el comprobante es viejo y no tiene el detalle por alícuota, se deduce de la alícuota general.</summary>
    public decimal Iva105 => PorAlicuota(MontoIvaTipoAfip4, 10.5m);
    public decimal Iva21 => PorAlicuota(MontoIvaTipoAfip5, 21m);
    public decimal Iva27 => PorAlicuota(MontoIvaTipoAfip6, 27m);

    private bool SinDetalleDeAlicuotas =>
        (MontoIvaTipoAfip4 ?? 0) == 0 && (MontoIvaTipoAfip5 ?? 0) == 0 && (MontoIvaTipoAfip6 ?? 0) == 0;

    private decimal PorAlicuota(decimal? monto, decimal alicuota)
    {
        if (!SinDetalleDeAlicuotas) return monto ?? 0;
        return MontoIva != 0 && Iva == alicuota ? MontoIva : 0;
    }
}

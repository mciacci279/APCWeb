using APC.Core.Datos;
using APC.Core.Modelos;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Consultas de ventas para la pantalla de reportes y el Libro IVA Ventas.
/// Las notas de crédito restan; las notas de pedido (sin CAE) se cuentan aparte porque no son fiscales.
/// </summary>
public class ReporteService
{
    /// <summary>Comprobante fiscal: tiene CAE de ARCA.</summary>
    private const string EsFiscal = "f.CAE IS NOT NULL AND LTRIM(f.CAE) <> ''";
    private const string EsNotaPedido = "f.CAE IS NULL AND ISNULL(f.Impresa, 0) = 1";
    private const string Periodo =
        "f.Fecha >= @desde AND f.Fecha < @hasta AND (@puntoVenta IS NULL OR f.Terminal = @puntoVenta)";

    private static object Rango(DateTime desde, DateTime hasta, int? puntoVenta) =>
        new { desde = desde.Date, hasta = hasta.Date.AddDays(1), puntoVenta };

    public async Task<ResumenVentas> ResumenAsync(string cs, DateTime desde, DateTime hasta, int? puntoVenta = null)
    {
        using var cn = await Db.AbrirAsync(cs);
        var r = await cn.QueryFirstAsync<ResumenVentas>(
            $@"SELECT
                 (SELECT COUNT(*) FROM dbo.Factura f WHERE {Periodo} AND {EsFiscal}) AS CantidadFacturas,
                 (SELECT ISNULL(SUM(f.MontoTotal), 0) FROM dbo.Factura f WHERE {Periodo} AND {EsFiscal}) AS TotalFacturas,
                 (SELECT ISNULL(SUM(f.MontoIva), 0) FROM dbo.Factura f WHERE {Periodo} AND {EsFiscal}) AS IvaFacturas,
                 (SELECT COUNT(*) FROM dbo.Factura f WHERE {Periodo} AND {EsNotaPedido} AND ISNULL(f.Anulada, 0) = 0) AS CantidadNotasPedido,
                 (SELECT ISNULL(SUM(f.MontoTotal), 0) FROM dbo.Factura f WHERE {Periodo} AND {EsNotaPedido} AND ISNULL(f.Anulada, 0) = 0) AS TotalNotasPedido,
                 (SELECT COUNT(*) FROM dbo.NotaCredito f WHERE {Periodo} AND {EsFiscal}) AS CantidadNotasCredito,
                 (SELECT ISNULL(SUM(f.MontoTotal), 0) FROM dbo.NotaCredito f WHERE {Periodo} AND {EsFiscal}) AS TotalNotasCredito,
                 (SELECT ISNULL(SUM(f.MontoIva), 0) FROM dbo.NotaCredito f WHERE {Periodo} AND {EsFiscal}) AS IvaNotasCredito",
            Rango(desde, hasta, puntoVenta));
        return r;
    }

    /// <summary>
    /// Los últimos comprobantes del período (facturas con CAE y notas de pedido), para la tabla de la pantalla de reportes.
    /// </summary>
    public async Task<List<ComprobanteListado>> UltimosAsync(string cs, DateTime desde, DateTime hasta, int? puntoVenta = null, int top = 8)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<ComprobanteListado>(
            $@"SELECT TOP (@top) f.Id, f.Fecha, f.Tipo, f.Terminal, f.Numero, f.ClienteNombre, f.MontoTotal, f.CAE,
                     fp.Nombre AS FormaPago, f.NumeroInterno, ISNULL(f.Impresa, 0) AS Impresa, ISNULL(f.Anulada, 0) AS MarcadaAnulada
               FROM dbo.Factura f LEFT JOIN dbo.FormaPago fp ON fp.Id = f.FormaPagoId
               WHERE {Periodo} AND ({EsFiscal} OR {EsNotaPedido})
               ORDER BY f.Fecha DESC",
            new { desde = desde.Date, hasta = hasta.Date.AddDays(1), puntoVenta, top })).ToList();
    }

    public async Task<List<FilaReporte>> PorDiaAsync(string cs, DateTime desde, DateTime hasta, int? puntoVenta = null)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<FilaReporte>(
            $@"SELECT CONVERT(VARCHAR(10), f.Fecha, 103) AS Etiqueta, COUNT(*) AS Cantidad, SUM(f.MontoTotal) AS Total
               FROM dbo.Factura f
               WHERE {Periodo} AND {EsFiscal}
               GROUP BY CONVERT(VARCHAR(10), f.Fecha, 103), CAST(f.Fecha AS DATE)
               ORDER BY CAST(f.Fecha AS DATE)", Rango(desde, hasta, puntoVenta))).ToList();
    }

    public async Task<List<FilaReporte>> PorFormaPagoAsync(string cs, DateTime desde, DateTime hasta, int? puntoVenta = null)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<FilaReporte>(
            $@"SELECT ISNULL(fp.Nombre, '(sin forma de pago)') AS Etiqueta, COUNT(*) AS Cantidad, SUM(f.MontoTotal) AS Total
               FROM dbo.Factura f LEFT JOIN dbo.FormaPago fp ON fp.Id = f.FormaPagoId
               WHERE {Periodo} AND ({EsFiscal} OR ({EsNotaPedido} AND ISNULL(f.Anulada, 0) = 0))
               GROUP BY fp.Nombre
               ORDER BY SUM(f.MontoTotal) DESC", Rango(desde, hasta, puntoVenta))).ToList();
    }

    public async Task<List<FilaReporte>> PorUsuarioAsync(string cs, DateTime desde, DateTime hasta, int? puntoVenta = null)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<FilaReporte>(
            $@"SELECT ISNULL(u.NombreUsuario, '(sin usuario)') AS Etiqueta, ISNULL(u.Nombre, '') AS Detalle,
                      COUNT(*) AS Cantidad, SUM(f.MontoTotal) AS Total
               FROM dbo.Factura f LEFT JOIN dbo.Usuario u ON u.Id = f.UsuarioId
               WHERE {Periodo} AND ({EsFiscal} OR ({EsNotaPedido} AND ISNULL(f.Anulada, 0) = 0))
               GROUP BY u.NombreUsuario, u.Nombre
               ORDER BY SUM(f.MontoTotal) DESC", Rango(desde, hasta, puntoVenta))).ToList();
    }

    /// <summary>Lo vendido por producto. Lo devuelto con nota de crédito se resta.</summary>
    public async Task<List<FilaProducto>> PorProductoAsync(string cs, DateTime desde, DateTime hasta, int? puntoVenta = null, int top = 100)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<FilaProducto>(
            $@"SELECT TOP (@top) v.Codigo, v.Producto, SUM(v.Cantidad) AS Cantidad, SUM(v.Total) AS Total
               FROM (
                 SELECT p.Codigo, p.Nombre AS Producto, d.CantidadRenglon AS Cantidad, d.MontoRenglon AS Total
                 FROM dbo.Detalle d
                 JOIN dbo.Factura f ON f.Id = d.FacturaId
                 JOIN dbo.Producto p ON p.Id = d.ProductoId
                 WHERE {Periodo} AND ({EsFiscal} OR ({EsNotaPedido} AND ISNULL(f.Anulada, 0) = 0))
                 UNION ALL
                 SELECT p.Codigo, p.Nombre, -d.CantidadRenglon, -d.MontoRenglon
                 FROM dbo.DetalleNotaCredito d
                 JOIN dbo.NotaCredito f ON f.Id = d.NotaCreditoId
                 JOIN dbo.Producto p ON p.Id = d.ProductoId
                 WHERE {Periodo} AND {EsFiscal}
               ) v
               GROUP BY v.Codigo, v.Producto
               ORDER BY SUM(v.Total) DESC",
            new { desde = desde.Date, hasta = hasta.Date.AddDays(1), puntoVenta, top })).ToList();
    }

    /// <summary>Libro IVA Ventas: facturas y notas de crédito con CAE, ordenadas por fecha.</summary>
    public async Task<List<FilaLibroIva>> LibroIvaVentasAsync(string cs, DateTime desde, DateTime hasta, int? puntoVenta = null)
    {
        using var cn = await Db.AbrirAsync(cs);
        var filas = (await cn.QueryAsync<FilaLibroIva>(
            $@"SELECT f.Fecha, 'Factura' AS Comprobante, f.TipoComprobanteAFIP, f.Tipo AS Letra, f.Terminal, f.Numero,
                      f.ClienteNombre, f.ClienteCUIT, i.Nombre AS CondicionIva, f.CAE,
                      f.MontoTotal, f.MontoIva, f.MontoNoGravado, f.MontoExento,
                      f.MontoIvaTipoAfip4, f.MontoIvaTipoAfip5, f.MontoIvaTipoAfip6, f.Iva
               FROM dbo.Factura f LEFT JOIN dbo.Iva i ON i.Id = f.IvaId
               WHERE {Periodo} AND {EsFiscal}
               UNION ALL
               SELECT f.Fecha, 'Nota de crédito', f.TipoComprobanteAFIP, f.Tipo, f.Terminal, f.Numero,
                      f.ClienteNombre, f.ClienteCUIT, i.Nombre, f.CAE,
                      -f.MontoTotal, -f.MontoIva, -f.MontoNoGravado, -f.MontoExento,
                      -f.MontoIvaTipoAfip4, -f.MontoIvaTipoAfip5, -f.MontoIvaTipoAfip6, f.Iva
               FROM dbo.NotaCredito f LEFT JOIN dbo.Iva i ON i.Id = f.IvaId
               WHERE {Periodo} AND {EsFiscal}",
            Rango(desde, hasta, puntoVenta))).ToList();

        return filas.OrderBy(f => f.Fecha).ThenBy(f => f.Numero).ToList();
    }
}

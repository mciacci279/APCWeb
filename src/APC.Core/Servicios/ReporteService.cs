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

    /// <summary>
    /// Ventas netas del período para los gráficos: facturas con CAE y notas de pedido suman (+1 comprobante),
    /// las notas de crédito con CAE restan (−1 y su importe en negativo). Así una factura anulada con nota de
    /// crédito no infla los números. <paramref name="clave"/> es la columna por la que se agrupa (forma de pago, usuario) o NULL.
    /// </summary>
    private static string Ventas(string clave) =>
        $@"SELECT f.Fecha, {clave} AS Clave, 1 AS Cant, f.MontoTotal AS Monto
           FROM dbo.Factura f
           WHERE {Periodo} AND ({EsFiscal} OR ({EsNotaPedido} AND ISNULL(f.Anulada, 0) = 0))
           UNION ALL
           SELECT f.Fecha, {clave}, -1, -f.MontoTotal
           FROM dbo.NotaCredito f
           WHERE {Periodo} AND {EsFiscal}";

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
    /// Los últimos comprobantes del período (facturas con CAE, notas de pedido y notas de crédito con CAE),
    /// para la tabla de la pantalla de reportes. Las facturas anuladas con nota de crédito vienen marcadas.
    /// </summary>
    public async Task<List<ComprobanteListado>> UltimosAsync(string cs, DateTime desde, DateTime hasta, int? puntoVenta = null, int top = 8)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<ComprobanteListado>(
            $@"SELECT TOP (@top) * FROM (
                 SELECT f.Id, f.Fecha, f.Tipo, f.Terminal, f.Numero, f.ClienteNombre, f.MontoTotal, f.CAE,
                        fp.Nombre AS FormaPago, nc.NcId, nc.NcTipo, nc.NcTerminal, nc.NcNumero, nc.NcCAE,
                        f.NumeroInterno, ISNULL(f.Impresa, 0) AS Impresa, ISNULL(f.Anulada, 0) AS MarcadaAnulada,
                        CAST(0 AS BIT) AS EsNotaCredito
                 FROM dbo.Factura f LEFT JOIN dbo.FormaPago fp ON fp.Id = f.FormaPagoId
                 OUTER APPLY (SELECT TOP 1 n.Id AS NcId, n.Tipo AS NcTipo, n.Terminal AS NcTerminal, n.Numero AS NcNumero, n.CAE AS NcCAE
                              FROM dbo.NotaCredito n
                              WHERE n.TipoFacturaRelacionada = f.Tipo AND n.TerminalFacturaRelacionada = f.Terminal
                                AND n.NroFacturaRelacionada = f.Numero AND ISNULL(n.Anulada, 0) = 0
                              ORDER BY n.Fecha DESC) nc
                 WHERE {Periodo} AND ({EsFiscal} OR {EsNotaPedido})
                 UNION ALL
                 SELECT f.Id, f.Fecha, f.Tipo, f.Terminal, f.Numero, f.ClienteNombre, -f.MontoTotal, f.CAE,
                        fp.Nombre, NULL, NULL, NULL, NULL, NULL,
                        NULL, ISNULL(f.Impresa, 0), ISNULL(f.Anulada, 0), CAST(1 AS BIT)
                 FROM dbo.NotaCredito f LEFT JOIN dbo.FormaPago fp ON fp.Id = f.FormaPagoId
                 WHERE {Periodo} AND {EsFiscal}
               ) x
               ORDER BY x.Fecha DESC",
            new { desde = desde.Date, hasta = hasta.Date.AddDays(1), puntoVenta, top })).ToList();
    }

    public async Task<List<FilaReporte>> PorDiaAsync(string cs, DateTime desde, DateTime hasta, int? puntoVenta = null)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<FilaReporte>(
            // Facturas con CAE y notas de pedido (sin CAE) no anuladas: el mismo criterio que "Por forma de pago",
            // así los dos totales del período coinciden.
            // Las notas de crédito restan el día en que se emitieron (una factura anulada el mismo día da 0).
            $@"SELECT CONVERT(VARCHAR(10), v.Fecha, 103) AS Etiqueta, SUM(v.Cant) AS Cantidad, SUM(v.Monto) AS Total
               FROM ({Ventas("NULL")}) v
               GROUP BY CONVERT(VARCHAR(10), v.Fecha, 103), CAST(v.Fecha AS DATE)
               ORDER BY CAST(v.Fecha AS DATE)", Rango(desde, hasta, puntoVenta))).ToList();
    }

    public async Task<List<FilaReporte>> PorFormaPagoAsync(string cs, DateTime desde, DateTime hasta, int? puntoVenta = null)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<FilaReporte>(
            $@"SELECT ISNULL(fp.Nombre, '(sin forma de pago)') AS Etiqueta, SUM(v.Cant) AS Cantidad, SUM(v.Monto) AS Total
               FROM ({Ventas("f.FormaPagoId")}) v LEFT JOIN dbo.FormaPago fp ON fp.Id = v.Clave
               GROUP BY fp.Nombre
               HAVING SUM(v.Cant) <> 0 OR SUM(v.Monto) <> 0
               ORDER BY SUM(v.Monto) DESC", Rango(desde, hasta, puntoVenta))).ToList();
    }

    public async Task<List<FilaReporte>> PorUsuarioAsync(string cs, DateTime desde, DateTime hasta, int? puntoVenta = null)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<FilaReporte>(
            $@"SELECT ISNULL(u.NombreUsuario, '(sin usuario)') AS Etiqueta, ISNULL(u.Nombre, '') AS Detalle,
                      SUM(v.Cant) AS Cantidad, SUM(v.Monto) AS Total
               FROM ({Ventas("f.UsuarioId")}) v LEFT JOIN dbo.Usuario u ON u.Id = v.Clave
               GROUP BY u.NombreUsuario, u.Nombre
               HAVING SUM(v.Cant) <> 0 OR SUM(v.Monto) <> 0
               ORDER BY SUM(v.Monto) DESC", Rango(desde, hasta, puntoVenta))).ToList();
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

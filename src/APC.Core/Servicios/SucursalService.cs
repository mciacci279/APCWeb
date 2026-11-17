using System.Data;
using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Tenants;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Sucursales y stock por sucursal. Producto.Stock se mantiene como el total de todas las
/// sucursales, para que los reportes y el sistema de escritorio sigan viendo lo mismo de siempre.
/// </summary>
public class SucursalService
{
    /// <summary>Columna agregada por el script 16: puede no existir todavía.</summary>
    private static async Task<string> ColumnaExigeCajaAsync(IDbConnection cn) =>
        await cn.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN COL_LENGTH('dbo.Sucursal', 'ExigeCaja') IS NULL THEN 0 ELSE 1 END") == 1
            ? "ExigeCaja"
            : "CAST(1 AS BIT) AS ExigeCaja";

    public async Task<List<Sucursal>> ListarAsync(string cs, bool incluirInactivas = true)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<Sucursal>(
            $@"SELECT Id, Nombre, PuntoVenta, Domicilio, Localidad, Telefono, LeyendaTicket, Activa, FechaAlta,
                      {await ColumnaExigeCajaAsync(cn)}
               FROM dbo.Sucursal
               {(incluirInactivas ? "" : "WHERE Activa = 1")}
               ORDER BY PuntoVenta")).ToList();
    }

    public async Task<Sucursal> ObtenerAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.QueryFirstOrDefaultAsync<Sucursal>(
            $@"SELECT Id, Nombre, PuntoVenta, Domicilio, Localidad, Telefono, LeyendaTicket, Activa, FechaAlta,
                      {await ColumnaExigeCajaAsync(cn)}
               FROM dbo.Sucursal WHERE Id = @id", new { id });
    }

    /// <summary>
    /// La primera vez que se usa el sistema no hay ninguna sucursal cargada: se crea
    /// «Casa central» con el punto de venta de la empresa y se le asigna todo el stock actual.
    /// </summary>
    public async Task<Sucursal> AsegurarCasaCentralAsync(TenantConfig tenant)
    {
        using var cn = await Db.AbrirAsync(tenant.ConnectionString);
        var existente = await cn.QueryFirstOrDefaultAsync<Sucursal>(
            $@"SELECT TOP 1 Id, Nombre, PuntoVenta, LeyendaTicket, Activa, {await ColumnaExigeCajaAsync(cn)}
               FROM dbo.Sucursal ORDER BY PuntoVenta");
        if (existente != null) return existente;

        var id = Guid.NewGuid();
        using (var tx = cn.BeginTransaction())
        {
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.Sucursal (Id, Nombre, PuntoVenta, Activa, FechaAlta)
                  VALUES (@id, N'Casa central', @pv, 1, GETDATE())",
                new { id, pv = tenant.PuntoVenta }, tx);

            await cn.ExecuteAsync(
                @"INSERT INTO dbo.StockSucursal (SucursalId, ProductoId, Stock, StockMinimo)
                  SELECT @id, p.Id, ISNULL(p.Stock, 0), p.StockMinimo FROM dbo.Producto p",
                new { id }, tx);

            tx.Commit();
        }

        return new Sucursal { Id = id, Nombre = "Casa central", PuntoVenta = tenant.PuntoVenta, Activa = true };
    }

    public async Task<string> GuardarAsync(string cs, Sucursal s)
    {
        s.Nombre = s.Nombre?.Trim();
        if (string.IsNullOrWhiteSpace(s.Nombre)) return "Escribí el nombre de la sucursal.";
        if (s.PuntoVenta < 1 || s.PuntoVenta > 99999) return "El punto de venta debe estar entre 1 y 99999.";

        using var cn = await Db.AbrirAsync(cs);
        var repetido = await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Sucursal WHERE PuntoVenta = @pv AND Id <> @id",
            new { pv = s.PuntoVenta, id = s.Id });
        if (repetido > 0) return "Ya hay otra sucursal con ese punto de venta.";

        if (s.EsNueva)
        {
            s.Id = Guid.NewGuid();
            using var tx = cn.BeginTransaction();
            var conExigeCaja = await ColumnaExigeCajaAsync(cn) == "ExigeCaja";
            await cn.ExecuteAsync(
                $@"INSERT INTO dbo.Sucursal (Id, Nombre, PuntoVenta, Domicilio, Localidad, Telefono, LeyendaTicket, Activa, FechaAlta
                           {(conExigeCaja ? ", ExigeCaja" : "")})
                   VALUES (@Id, @Nombre, @PuntoVenta, @Domicilio, @Localidad, @Telefono, @LeyendaTicket, 1, GETDATE()
                           {(conExigeCaja ? ", @ExigeCaja" : "")})", s, tx);

            // La sucursal nueva arranca con todos los productos en cero.
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.StockSucursal (SucursalId, ProductoId, Stock)
                  SELECT @Id, p.Id, 0 FROM dbo.Producto p", new { s.Id }, tx);
            tx.Commit();
        }
        else
        {
            var conExigeCaja = await ColumnaExigeCajaAsync(cn) == "ExigeCaja";
            await cn.ExecuteAsync(
                $@"UPDATE dbo.Sucursal SET Nombre = @Nombre, PuntoVenta = @PuntoVenta, Domicilio = @Domicilio,
                          Localidad = @Localidad, Telefono = @Telefono, LeyendaTicket = @LeyendaTicket, Activa = @Activa
                          {(conExigeCaja ? ", ExigeCaja = @ExigeCaja" : "")}
                   WHERE Id = @Id", s);
        }
        return null;
    }

    public async Task<string> CambiarEstadoAsync(string cs, Guid id, bool activa)
    {
        using var cn = await Db.AbrirAsync(cs);
        if (!activa)
        {
            var activas = await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Sucursal WHERE Activa = 1");
            if (activas <= 1) return "Tiene que quedar al menos una sucursal activa.";
        }
        await cn.ExecuteAsync("UPDATE dbo.Sucursal SET Activa = @activa WHERE Id = @id", new { id, activa });
        return null;
    }

    // ------------------------------------------------------------------ stock

    /// <summary>Stock del producto en cada sucursal.</summary>
    public async Task<List<StockEnSucursal>> StockDeProductoAsync(string cs, Guid productoId)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<StockEnSucursal>(
            @"SELECT s.Id AS SucursalId, s.Nombre AS Sucursal, s.PuntoVenta,
                     ISNULL(ss.Stock, 0) AS Stock, ss.StockMinimo
              FROM dbo.Sucursal s
              LEFT JOIN dbo.StockSucursal ss ON ss.SucursalId = s.Id AND ss.ProductoId = @productoId
              WHERE s.Activa = 1
              ORDER BY s.PuntoVenta", new { productoId })).ToList();
    }

    /// <summary>Suma o resta stock en una sucursal y mantiene al día el total de Producto.Stock.</summary>
    /// <param name="actualizarTotal">
    /// Si también hay que sumar la cantidad a Producto.Stock. Se pone en false cuando quien llama
    /// ya actualizó el total por su cuenta.
    /// </param>
    public static async Task MoverAsync(IDbConnection cn, IDbTransaction tx, Guid sucursalId, Guid productoId,
        decimal cantidad, string tipo, Guid? usuarioId, string referencia, string observaciones,
        bool registrar = true, bool actualizarTotal = true)
    {
        await cn.ExecuteAsync(
            @"IF NOT EXISTS (SELECT 1 FROM dbo.StockSucursal WHERE SucursalId = @sucursalId AND ProductoId = @productoId)
                  INSERT INTO dbo.StockSucursal (SucursalId, ProductoId, Stock) VALUES (@sucursalId, @productoId, 0);
              UPDATE dbo.StockSucursal SET Stock = Stock + @cantidad
                  WHERE SucursalId = @sucursalId AND ProductoId = @productoId;",
            new { sucursalId, productoId, cantidad }, tx);

        if (actualizarTotal)
            await cn.ExecuteAsync("UPDATE dbo.Producto SET Stock = ISNULL(Stock, 0) + @cantidad WHERE Id = @productoId",
                new { productoId, cantidad }, tx);

        if (!registrar) return;

        await cn.ExecuteAsync(
            @"INSERT INTO dbo.MovimientoStock (Id, Fecha, SucursalId, ProductoId, Cantidad, Tipo, UsuarioId, Referencia, Observaciones)
              VALUES (NEWID(), GETDATE(), @sucursalId, @productoId, @cantidad, @tipo, @usuarioId, @referencia, @observaciones)",
            new { sucursalId, productoId, cantidad, tipo, usuarioId, referencia, observaciones }, tx);
    }

    /// <summary>Descuenta lo vendido. No registra movimiento: la venta ya queda en Factura y Detalle.</summary>
    public static async Task DescontarVentaAsync(IDbConnection cn, IDbTransaction tx, Guid sucursalId, Guid productoId, decimal cantidad)
    {
        await cn.ExecuteAsync(
            @"IF NOT EXISTS (SELECT 1 FROM dbo.StockSucursal WHERE SucursalId = @sucursalId AND ProductoId = @productoId)
                  INSERT INTO dbo.StockSucursal (SucursalId, ProductoId, Stock) VALUES (@sucursalId, @productoId, 0);
              UPDATE dbo.StockSucursal
                  SET Stock = CASE WHEN Stock - @cantidad <= 0 THEN 0 ELSE Stock - @cantidad END
                  WHERE SucursalId = @sucursalId AND ProductoId = @productoId;
              UPDATE dbo.Producto
                  SET Stock = CASE WHEN ISNULL(Stock, 0) - @cantidad <= 0 THEN 0 ELSE Stock - @cantidad END
                  WHERE Id = @productoId;",
            new { sucursalId, productoId, cantidad }, tx);
    }

    public async Task<string> AjustarAsync(string cs, Guid usuarioId, Guid sucursalId, Guid productoId,
        decimal cantidad, string observaciones)
    {
        if (cantidad == 0) return "La cantidad no puede ser cero.";
        using var cn = await Db.AbrirAsync(cs);
        using var tx = cn.BeginTransaction();
        await MoverAsync(cn, tx, sucursalId, productoId, cantidad, TipoMovimientoStock.Ajuste, usuarioId, null, observaciones);
        tx.Commit();
        return null;
    }

    /// <summary>Mueve mercadería de una sucursal a otra. El stock total no cambia.</summary>
    public async Task<string> TransferirAsync(string cs, Guid usuarioId, Guid origenId, Guid destinoId,
        Guid productoId, decimal cantidad, string observaciones)
    {
        if (origenId == destinoId) return "El origen y el destino tienen que ser distintos.";
        if (cantidad <= 0) return "La cantidad tiene que ser mayor que cero.";

        using var cn = await Db.AbrirAsync(cs);
        var disponible = await cn.ExecuteScalarAsync<decimal?>(
            "SELECT Stock FROM dbo.StockSucursal WHERE SucursalId = @origenId AND ProductoId = @productoId",
            new { origenId, productoId }) ?? 0;
        if (disponible < cantidad)
            return $"La sucursal de origen tiene {disponible:0.###} y se quieren transferir {cantidad:0.###}.";

        using var tx = cn.BeginTransaction();
        var referencia = "Transferencia " + DateTime.Now.ToString("yyyyMMddHHmmss");
        // El total de Producto.Stock no debe cambiar: una salida y una entrada por la misma cantidad.
        await MoverAsync(cn, tx, origenId, productoId, -cantidad, TipoMovimientoStock.TransferenciaSalida, usuarioId, referencia, observaciones);
        await MoverAsync(cn, tx, destinoId, productoId, cantidad, TipoMovimientoStock.TransferenciaEntrada, usuarioId, referencia, observaciones);
        tx.Commit();
        return null;
    }

    public async Task<List<MovimientoStockItem>> MovimientosAsync(string cs, DateTime desde, DateTime hasta,
        Guid? sucursalId, Guid? productoId, int max = 300)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<MovimientoStockItem>(
            @"SELECT TOP (@max) m.Fecha, s.Nombre AS Sucursal, p.Nombre AS Producto, p.Codigo,
                     m.Cantidad, m.Tipo, u.NombreUsuario AS Usuario, m.Referencia, m.Observaciones
              FROM dbo.MovimientoStock m
              LEFT JOIN dbo.Sucursal s ON s.Id = m.SucursalId
              LEFT JOIN dbo.Producto p ON p.Id = m.ProductoId
              LEFT JOIN dbo.Usuario u ON u.Id = m.UsuarioId
              WHERE m.Fecha >= @desde AND m.Fecha < @hasta
                AND (@sucursalId IS NULL OR m.SucursalId = @sucursalId)
                AND (@productoId IS NULL OR m.ProductoId = @productoId)
              ORDER BY m.Fecha DESC",
            new { desde = desde.Date, hasta = hasta.Date.AddDays(1), sucursalId, productoId, max })).ToList();
    }

    /// <summary>Productos por debajo del mínimo en una sucursal.</summary>
    public async Task<List<StockEnSucursal>> BajoMinimoAsync(string cs, Guid sucursalId)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<StockEnSucursal>(
            @"SELECT s.Id AS SucursalId, s.Nombre AS Sucursal, s.PuntoVenta, ss.Stock,
                     ISNULL(ss.StockMinimo, p.StockMinimo) AS StockMinimo
              FROM dbo.StockSucursal ss
              JOIN dbo.Sucursal s ON s.Id = ss.SucursalId
              JOIN dbo.Producto p ON p.Id = ss.ProductoId
              WHERE ss.SucursalId = @sucursalId
                AND ISNULL(ss.StockMinimo, p.StockMinimo) > 0
                AND ss.Stock <= ISNULL(ss.StockMinimo, p.StockMinimo)
              ORDER BY ss.Stock", new { sucursalId })).ToList();
    }
}

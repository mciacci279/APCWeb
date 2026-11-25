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

    /// <summary>Columnas agregadas por el script 23: pueden no existir todavía.</summary>
    private static async Task<bool> ExisteQrPagoAsync(IDbConnection cn) =>
        await cn.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN COL_LENGTH('dbo.Sucursal', 'QrPago') IS NULL THEN 0 ELSE 1 END") == 1;

    /// <summary>Columna agregada por el script 20: puede no existir todavía.</summary>
    private static async Task<bool> ExisteBorradaAsync(IDbConnection cn) =>
        await cn.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN COL_LENGTH('dbo.Sucursal', 'Borrada') IS NULL THEN 0 ELSE 1 END") == 1;

    public async Task<List<Sucursal>> ListarAsync(string cs, bool incluirInactivas = true, bool incluirBorradas = false)
    {
        using var cn = await Db.AbrirAsync(cs);
        var conBorrada = await ExisteBorradaAsync(cn);
        var columnaBorrada = conBorrada ? "Borrada" : "CAST(0 AS BIT) AS Borrada";

        var condiciones = new List<string>();
        if (!incluirInactivas) condiciones.Add("Activa = 1");
        if (conBorrada && !incluirBorradas) condiciones.Add("Borrada = 0");
        var donde = condiciones.Count == 0 ? "" : "WHERE " + string.Join(" AND ", condiciones);

        var conQr = await ExisteQrPagoAsync(cn);
        var columnasQr = conQr
            ? "QrPagoAlias, CAST(CASE WHEN QrPago IS NULL THEN 0 ELSE 1 END AS BIT) AS TieneQrPago"
            : "CAST(NULL AS NVARCHAR(100)) AS QrPagoAlias, CAST(0 AS BIT) AS TieneQrPago";

        return (await cn.QueryAsync<Sucursal>(
            $@"SELECT Id, Nombre, PuntoVenta, Domicilio, Localidad, Telefono, LeyendaTicket, Activa, FechaAlta,
                      {await ColumnaExigeCajaAsync(cn)}, {columnaBorrada}, {columnasQr}
               FROM dbo.Sucursal
               {donde}
               ORDER BY PuntoVenta")).ToList();
    }

    public async Task<Sucursal> ObtenerAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        var columnasQr = await ExisteQrPagoAsync(cn)
            ? "QrPagoAlias, CAST(CASE WHEN QrPago IS NULL THEN 0 ELSE 1 END AS BIT) AS TieneQrPago"
            : "CAST(NULL AS NVARCHAR(100)) AS QrPagoAlias, CAST(0 AS BIT) AS TieneQrPago";

        return await cn.QueryFirstOrDefaultAsync<Sucursal>(
            $@"SELECT Id, Nombre, PuntoVenta, Domicilio, Localidad, Telefono, LeyendaTicket, Activa, FechaAlta,
                      {await ColumnaExigeCajaAsync(cn)}, {columnasQr}
               FROM dbo.Sucursal WHERE Id = @id", new { id });
    }

    // ------------------------------------------------------------------ QR propio del comercio

    /// <summary>Tamaño máximo de la imagen del QR: con 500 KB sobra para una captura.</summary>
    public const long QrMaximoBytes = 500 * 1024;

    /// <summary>
    /// Guarda el QR interoperable que el comercio sacó de su banco o de MODO.
    /// Con imagen en null solo cambia el alias; para sacarlo está QuitarQrAsync.
    /// </summary>
    public async Task<string> GuardarQrAsync(string cs, Guid id, byte[] imagen, string tipo, string alias)
    {
        using var cn = await Db.AbrirAsync(cs);
        if (!await ExisteQrPagoAsync(cn))
            return "Falta correr el script 23 en esta base. Lo podés hacer desde el panel de base de datos.";

        alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        if (alias != null && alias.Length > 100) return "El alias no puede pasar de 100 caracteres.";

        if (imagen == null)
            await cn.ExecuteAsync("UPDATE dbo.Sucursal SET QrPagoAlias = @alias WHERE Id = @id", new { id, alias });
        else
            await cn.ExecuteAsync(
                "UPDATE dbo.Sucursal SET QrPago = @imagen, QrPagoTipo = @tipo, QrPagoAlias = @alias WHERE Id = @id",
                new { id, imagen, tipo, alias });
        return null;
    }

    public async Task<string> QuitarQrAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        if (!await ExisteQrPagoAsync(cn)) return null;
        await cn.ExecuteAsync("UPDATE dbo.Sucursal SET QrPago = NULL, QrPagoTipo = NULL WHERE Id = @id", new { id });
        return null;
    }

    /// <summary>El QR listo para mostrar en pantalla. Null si la sucursal no tiene ninguno.</summary>
    public async Task<string> QrDataUriAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        if (!await ExisteQrPagoAsync(cn)) return null;

        var fila = await cn.QueryFirstOrDefaultAsync<QrGuardado>(
            "SELECT QrPago, QrPagoTipo FROM dbo.Sucursal WHERE Id = @id", new { id });
        if (fila?.QrPago == null || fila.QrPago.Length == 0) return null;

        var tipo = string.IsNullOrWhiteSpace(fila.QrPagoTipo) ? "image/png" : fila.QrPagoTipo;
        return $"data:{tipo};base64," + Convert.ToBase64String(fila.QrPago);
    }

    private class QrGuardado
    {
        public byte[] QrPago { get; set; }
        public string QrPagoTipo { get; set; }
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
               FROM dbo.Sucursal
               {(await ExisteBorradaAsync(cn) ? "WHERE Borrada = 0" : "")}
               ORDER BY PuntoVenta");
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
            $@"SELECT COUNT(*) FROM dbo.Sucursal
               WHERE PuntoVenta = @pv AND Id <> @id {(await ExisteBorradaAsync(cn) ? "AND Borrada = 0" : "")}",
            new { pv = s.PuntoVenta, id = s.Id });
        if (repetido > 0) return "Ya hay otra sucursal con ese punto de venta.";

        if (s.EsNueva)
        {
            s.Id = Guid.NewGuid();
            // Se averigua antes de abrir la transacción: con una transacción abierta,
            // una consulta sin transacción revienta la conexión.
            var conExigeCaja = await ColumnaExigeCajaAsync(cn) == "ExigeCaja";
            using var tx = cn.BeginTransaction();
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
            // Sin la columna, el tilde de la caja no se podría guardar y quedaría en "Sí" sin avisar.
            if (!conExigeCaja && !s.ExigeCaja)
                return "No se puede cambiar la apertura de caja: falta correr el script 16 en la base de esta empresa.";
            await cn.ExecuteAsync(
                $@"UPDATE dbo.Sucursal SET Nombre = @Nombre, PuntoVenta = @PuntoVenta, Domicilio = @Domicilio,
                          Localidad = @Localidad, Telefono = @Telefono, LeyendaTicket = @LeyendaTicket, Activa = @Activa
                          {(conExigeCaja ? ", ExigeCaja = @ExigeCaja" : "")}
                   WHERE Id = @Id", s);
        }
        return null;
    }

    /// <summary>
    /// Prende o apaga la apertura de caja de una sucursal. Lo puede hacer el administrador
    /// del negocio: es una decisión suya, no de ARCA.
    /// </summary>
    public async Task<string> CambiarExigeCajaAsync(string cs, Guid id, bool exige)
    {
        try
        {
            using var cn = await Db.AbrirAsync(cs);
            await cn.ExecuteAsync("UPDATE dbo.Sucursal SET ExigeCaja = @exige WHERE Id = @id", new { id, exige });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            return "No se pudo guardar. ¿Se ejecutó el script 16 en la base de esta empresa? Detalle: " + ex.Message;
        }
        return null;
    }

    /// <summary>
    /// Borrado lógico: la sucursal deja de aparecer en todo el sistema, pero la fila queda
    /// con su stock y sus movimientos, así los comprobantes viejos siguen resolviendo su
    /// nombre y se puede recuperar si fue un error.
    /// </summary>
    public async Task<string> BorrarAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        if (!await ExisteBorradaAsync(cn))
            return "Falta correr el script 20 en la base de esta empresa para poder borrar sucursales.";

        var s = await cn.QueryFirstOrDefaultAsync<Sucursal>(
            "SELECT Id, Nombre, PuntoVenta FROM dbo.Sucursal WHERE Id = @id", new { id });
        if (s == null) return "La sucursal ya no existe.";

        if (await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Sucursal WHERE Borrada = 0") <= 1)
            return "Tiene que quedar al menos una sucursal.";

        await cn.ExecuteAsync("UPDATE dbo.Sucursal SET Borrada = 1, Activa = 0 WHERE Id = @id", new { id });
        return null;
    }

    /// <summary>Vuelve a poner en uso una sucursal borrada.</summary>
    public async Task<string> RestaurarAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        if (!await ExisteBorradaAsync(cn))
            return "Falta correr el script 20 en la base de esta empresa.";

        var pv = await cn.ExecuteScalarAsync<int?>("SELECT PuntoVenta FROM dbo.Sucursal WHERE Id = @id", new { id });
        if (pv == null) return "La sucursal ya no existe.";

        var repetido = await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Sucursal WHERE PuntoVenta = @pv AND Id <> @id AND Borrada = 0",
            new { pv, id });
        if (repetido > 0) return "Ya hay otra sucursal usando ese punto de venta.";

        await cn.ExecuteAsync("UPDATE dbo.Sucursal SET Borrada = 0, Activa = 1 WHERE Id = @id", new { id });
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
                     p.StockMinimo AS StockMinimo
              FROM dbo.StockSucursal ss
              JOIN dbo.Sucursal s ON s.Id = ss.SucursalId
              JOIN dbo.Producto p ON p.Id = ss.ProductoId
              WHERE ss.SucursalId = @sucursalId
                AND ISNULL(p.OmitirControlStock, 0) = 0
                AND ISNULL(p.StockMinimo, 0) > 0
                AND ss.Stock <= p.StockMinimo
              ORDER BY ss.Stock", new { sucursalId })).ToList();
    }
}

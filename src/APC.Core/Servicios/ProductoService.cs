using System.Data;
using APC.Core.Datos;
using APC.Core.Modelos;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// ABM de productos y cambios de stock/precios. Cada cambio de precio o stock deja una fila
/// en dbo.ProductoCambioStock, igual que frmProductoCambioStockEdicion de APC.WinForms,
/// así el historial es el mismo desde la web y desde el escritorio.
/// </summary>
public class ProductoService
{
    private readonly CatalogoService _catalogo;

    public ProductoService(CatalogoService catalogo) => _catalogo = catalogo;

    private const string Columnas = @"p.Id, p.Codigo, p.Nombre, p.Stock, p.PrecioCosto, p.PrecioVenta1, p.PrecioVenta2,
        p.PrecioVenta3, p.StockMinimo, p.StockMaximo, p.RubroId, r.Nombre AS Rubro,
        ISNULL(p.IvaDiferenciado,0) AS IvaDiferenciado, ISNULL(p.IvaExento,0) AS IvaExento,
        ISNULL(p.OmitirControlStock,0) AS OmitirControlStock, ISNULL(p.Activo,1) AS Activo";

    public async Task<List<ProductoDetalle>> ListarAsync(string cs, string texto, Guid? rubroId, bool incluirInactivos, int max = 200)
    {
        using var cn = await Db.AbrirAsync(cs);
        var like = string.IsNullOrWhiteSpace(texto) ? null : "%" + texto.Trim().Replace("[", "[[]").Replace("%", "[%]") + "%";
        return (await cn.QueryAsync<ProductoDetalle>(
            $@"SELECT TOP (@max) {Columnas}
               FROM dbo.Producto p LEFT JOIN dbo.Rubro r ON r.Id = p.RubroId
               WHERE (@like IS NULL OR p.Nombre LIKE @like OR p.Codigo LIKE @like)
                 AND (@rubroId IS NULL OR p.RubroId = @rubroId)
                 AND (@inactivos = 1 OR ISNULL(p.Activo,1) = 1)
               ORDER BY p.Nombre",
            new { max, like, rubroId, inactivos = incluirInactivos })).ToList();
    }

    public async Task<ProductoDetalle> ObtenerAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.QueryFirstOrDefaultAsync<ProductoDetalle>(
            $"SELECT {Columnas} FROM dbo.Producto p LEFT JOIN dbo.Rubro r ON r.Id = p.RubroId WHERE p.Id = @id", new { id });
    }

    public async Task<List<RubroItem>> RubrosAsync(string cs)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<RubroItem>("SELECT Id, Nombre FROM dbo.Rubro ORDER BY Nombre")).ToList();
    }

    /// <summary>Porcentajes PORC_PVTA1..3 y títulos TITULO_PVTA1..3 de la tabla Configuracion.</summary>
    public async Task<PorcentajesListas> ListasAsync(string cs)
    {
        var cfg = await _catalogo.ConfiguracionAsync(cs);
        var l = new PorcentajesListas();
        decimal Num(string c) => cfg.TryGetValue(c, out var v) && v.Numero.HasValue ? (decimal)v.Numero.Value : 0m;
        string Txt(string c, string def) => cfg.TryGetValue(c, out var v) && !string.IsNullOrWhiteSpace(v.Texto) ? v.Texto.Trim() : def;
        l.Lista1 = Num("PORC_PVTA1"); l.Lista2 = Num("PORC_PVTA2"); l.Lista3 = Num("PORC_PVTA3");
        l.Titulo1 = Txt("TITULO_PVTA1", l.Titulo1); l.Titulo2 = Txt("TITULO_PVTA2", l.Titulo2); l.Titulo3 = Txt("TITULO_PVTA3", l.Titulo3);
        return l;
    }

    /// <summary>
    /// Calcula los precios de venta desde el costo, como btnActualizarPrecios_Click de APC:
    /// lista 1 = costo + PORC_PVTA1 %; listas 2 y 3 = lista 1 + PORC_PVTA2/3 %.
    /// </summary>
    public static (decimal Pv1, decimal Pv2, decimal Pv3) PreciosDesdeCosto(decimal costo, PorcentajesListas l)
    {
        var pv1 = Math.Round(costo + costo * (l.Lista1 / 100m), 2);
        var pv2 = Math.Round(pv1 + pv1 * (l.Lista2 / 100m), 2);
        var pv3 = Math.Round(pv1 + pv1 * (l.Lista3 / 100m), 2);
        return (pv1, pv2, pv3);
    }

    // ------------------------------------------------------------ alta / edición / baja

    /// <param name="sucursalId">Sucursal a la que entra el stock inicial. Null = sin sucursales.</param>
    public async Task<string> CrearAsync(string cs, Guid usuarioId, ProductoDetalle p, Guid? sucursalId = null)
    {
        var err = Validar(p);
        if (err != null) return err;
        using var cn = await Db.AbrirAsync(cs);
        if (await CodigoDuplicadoAsync(cn, p.Codigo, null)) return "Ya existe un producto con ese código.";

        using var tx = cn.BeginTransaction();
        p.Id = Guid.NewGuid();
        await cn.ExecuteAsync(
            @"INSERT INTO dbo.Producto
                (Id, Nombre, Codigo, Stock, PrecioCosto, PrecioVenta1, PrecioVenta2, PrecioVenta3,
                 StockMinimo, StockMaximo, EtiquetaImpresa, OmitirControlStock, RubroId, IvaDiferenciado, IvaExento, Activo)
              VALUES
                (@Id, @Nombre, @Codigo, @Stock, @PrecioCosto, @PrecioVenta1, @PrecioVenta2, @PrecioVenta3,
                 @StockMinimo, @StockMaximo, 0, @OmitirControlStock, @RubroId, @IvaDiferenciado, @IvaExento, 1)",
            Limpio(p), tx);
        await RegistrarHistorialAsync(cn, tx, p.Id, usuarioId, p.Stock, p.PrecioCosto, p.PrecioVenta1, p.PrecioVenta2, p.PrecioVenta3,
            "[Alta desde APC Web]");

        // El producto nuevo aparece en todas las sucursales; el stock inicial entra en la del usuario.
        if (sucursalId != null)
            await cn.ExecuteAsync(
                @"IF OBJECT_ID(N'dbo.StockSucursal', N'U') IS NOT NULL
                  INSERT INTO dbo.StockSucursal (SucursalId, ProductoId, Stock, StockMinimo)
                  SELECT s.Id, @producto, CASE WHEN s.Id = @sucursal THEN @stock ELSE 0 END, @minimo
                  FROM dbo.Sucursal s",
                new { producto = p.Id, sucursal = sucursalId, stock = p.Stock, minimo = p.StockMinimo }, tx);

        tx.Commit();
        return null;
    }

    /// <summary>
    /// Edita los datos descriptivos. Precios y stock NO se cambian acá:
    /// van por CambiarPrecioStockAsync para que queden en el historial.
    /// </summary>
    public async Task<string> ActualizarDatosAsync(string cs, ProductoDetalle p)
    {
        var err = Validar(p);
        if (err != null) return err;
        using var cn = await Db.AbrirAsync(cs);
        if (await CodigoDuplicadoAsync(cn, p.Codigo, p.Id)) return "Ya existe otro producto con ese código.";
        await cn.ExecuteAsync(
            @"UPDATE dbo.Producto SET Nombre = @Nombre, Codigo = @Codigo, StockMinimo = @StockMinimo, StockMaximo = @StockMaximo,
                     RubroId = @RubroId, IvaDiferenciado = @IvaDiferenciado, IvaExento = @IvaExento,
                     OmitirControlStock = @OmitirControlStock, Activo = @Activo
              WHERE Id = @Id", Limpio(p));
        return null;
    }

    /// <summary>Cambio de stock y/o precios de un producto (ingreso de mercadería, nuevo precio, ajuste).</summary>
    /// <param name="sucursalId">Sucursal donde entra (o sale) el stock. Null = sin sucursales.</param>
    public async Task<string> CambiarPrecioStockAsync(string cs, Guid usuarioId, Guid productoId, CambioProducto c,
        Guid? sucursalId = null)
    {
        if (c.PrecioCosto < 0 || c.PrecioVenta1 < 0 || c.PrecioVenta2 < 0 || c.PrecioVenta3 < 0)
            return "Los precios no pueden ser negativos.";
        using var cn = await Db.AbrirAsync(cs);
        using var tx = cn.BeginTransaction();
        var filas = await cn.ExecuteAsync(
            @"UPDATE dbo.Producto SET Stock = Stock + @cant, PrecioCosto = @costo,
                     PrecioVenta1 = @pv1, PrecioVenta2 = @pv2, PrecioVenta3 = @pv3
              WHERE Id = @id",
            new { id = productoId, cant = c.CantidadStock, costo = c.PrecioCosto, pv1 = c.PrecioVenta1, pv2 = c.PrecioVenta2, pv3 = c.PrecioVenta3 }, tx);
        if (filas == 0) return "El producto no existe.";

        if (sucursalId != null && c.CantidadStock != 0)
            await SucursalService.MoverAsync(cn, tx, sucursalId.Value, productoId, c.CantidadStock,
                TipoMovimientoStock.Ajuste, usuarioId, null, c.Observaciones, registrar: false, actualizarTotal: false);

        await RegistrarHistorialAsync(cn, tx, productoId, usuarioId, c.CantidadStock, c.PrecioCosto,
            c.PrecioVenta1, c.PrecioVenta2, c.PrecioVenta3, c.Observaciones);
        tx.Commit();
        return null;
    }

    /// <summary>
    /// Aumento (o baja) de precios por rubro o de todos los productos, en % o en $,
    /// como la "Actualización de Familia / de TODOS los Productos" de APC.
    /// Devuelve la cantidad de productos actualizados.
    /// </summary>
    public async Task<(int Cantidad, string Error)> AumentoMasivoAsync(string cs, Guid usuarioId, AumentoMasivo a)
    {
        if (a.Valor == 0) return (0, "Indicá el porcentaje o el monto del aumento.");
        if (a.Tipo == TipoAumento.Porcentaje && a.Valor <= -100) return (0, "El porcentaje no puede ser -100 % o menor.");

        using var cn = await Db.AbrirAsync(cs);
        using var tx = cn.BeginTransaction();
        var productos = (await cn.QueryAsync<ProductoDetalle>(
            @"SELECT Id, PrecioCosto, PrecioVenta1, PrecioVenta2, PrecioVenta3 FROM dbo.Producto
              WHERE ISNULL(Activo,1) = 1 AND (@rubro IS NULL OR RubroId = @rubro)",
            new { rubro = a.RubroId }, tx)).ToList();

        decimal Aplicar(decimal v) => Math.Max(0, Math.Round(a.Tipo == TipoAumento.Porcentaje ? v + v * (a.Valor / 100m) : v + a.Valor, 2));
        decimal? AplicarN(decimal? v) => v.HasValue && v.Value != 0 ? Aplicar(v.Value) : v;

        string prefijo = a.RubroId == null ? "[Actualización de TODOS los Productos] " : "[Actualización de Familia] ";
        string detalle = a.Tipo == TipoAumento.Porcentaje ? $"{a.Valor:0.##}%" : $"${a.Valor:0.##}";
        string obs = (prefijo + detalle + " " + (a.Observaciones ?? "")).Trim();

        int n = 0;
        foreach (var p in productos)
        {
            if (p.PrecioVenta1 == 0) continue; // precio libre ("varios"): no se toca

            // Igual que APC: en aumento en pesos, un costo en 0 no se toca
            var costo = !a.IncluirCosto ? p.PrecioCosto
                      : (a.Tipo == TipoAumento.Pesos && p.PrecioCosto == 0 ? 0 : Aplicar(p.PrecioCosto));
            var pv1 = Aplicar(p.PrecioVenta1);
            var pv2 = AplicarN(p.PrecioVenta2);
            var pv3 = AplicarN(p.PrecioVenta3);

            await cn.ExecuteAsync(
                "UPDATE dbo.Producto SET PrecioCosto = @costo, PrecioVenta1 = @pv1, PrecioVenta2 = @pv2, PrecioVenta3 = @pv3 WHERE Id = @id",
                new { id = p.Id, costo, pv1, pv2, pv3 }, tx);
            await RegistrarHistorialAsync(cn, tx, p.Id, usuarioId, 0, costo, pv1, pv2, pv3, obs);
            n++;
        }
        tx.Commit();
        return (n, null);
    }

    public async Task<List<CambioProductoHistorial>> HistorialAsync(string cs, Guid? productoId, DateTime? desde, DateTime? hasta, int max = 300)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<CambioProductoHistorial>(
            @"SELECT TOP (@max) c.Fecha, c.Cantidad, c.PU, c.PrecioVenta1, c.PrecioVenta2, c.PrecioVenta3, c.Observaciones,
                     u.NombreUsuario AS Usuario, p.Nombre AS Producto, p.Codigo
              FROM dbo.ProductoCambioStock c
              JOIN dbo.Producto p ON p.Id = c.ProductoId
              LEFT JOIN dbo.Usuario u ON u.Id = c.UsuarioId
              WHERE (@productoId IS NULL OR c.ProductoId = @productoId)
                AND (@desde IS NULL OR c.Fecha >= @desde)
                AND (@hasta IS NULL OR c.Fecha < @hasta)
              ORDER BY c.Fecha DESC",
            new { max, productoId, desde = desde?.Date, hasta = hasta?.Date.AddDays(1) })).ToList();
    }

    // ------------------------------------------------------------ auxiliares

    private static Task RegistrarHistorialAsync(IDbConnection cn, IDbTransaction tx, Guid productoId, Guid usuarioId,
        decimal cantidad, decimal costo, decimal pv1, decimal? pv2, decimal? pv3, string observaciones) =>
        cn.ExecuteAsync(
            @"INSERT INTO dbo.ProductoCambioStock
                (Id, Fecha, Cantidad, PU, Observaciones, ProductoId, UsuarioId, PrecioVenta1, PrecioVenta2, PrecioVenta3)
              VALUES (NEWID(), GETDATE(), @cantidad, @costo, @obs, @productoId, @usuarioId, @pv1, @pv2, @pv3)",
            new { cantidad, costo, obs = observaciones, productoId, usuarioId, pv1, pv2, pv3 }, tx);

    private static async Task<bool> CodigoDuplicadoAsync(IDbConnection cn, string codigo, Guid? excepto)
    {
        if (string.IsNullOrWhiteSpace(codigo)) return false;
        return await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Producto WHERE Codigo = @c AND (@id IS NULL OR Id <> @id)",
            new { c = codigo.Trim(), id = excepto }) > 0;
    }

    private static string Validar(ProductoDetalle p)
    {
        if (string.IsNullOrWhiteSpace(p.Nombre)) return "Falta la descripción del producto.";
        if (p.Nombre.Trim().Length > 100) return "La descripción no puede superar los 100 caracteres.";
        if (p.Codigo?.Trim().Length > 100) return "El código no puede superar los 100 caracteres.";
        if (p.IvaDiferenciado && p.IvaExento) return "Un producto no puede ser IVA diferenciado y exento a la vez.";
        if (p.PrecioCosto < 0 || p.PrecioVenta1 < 0) return "Los precios no pueden ser negativos.";
        return null;
    }

    private static ProductoDetalle Limpio(ProductoDetalle p)
    {
        p.Nombre = p.Nombre?.Trim();
        p.Codigo = string.IsNullOrWhiteSpace(p.Codigo) ? null : p.Codigo.Trim();
        return p;
    }
}

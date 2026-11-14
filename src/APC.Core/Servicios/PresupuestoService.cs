using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Tenants;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Presupuestos: mismas tablas que APC.WinForms (Presupuesto / DetallePresupuesto).
/// No piden CAE, no entran en el arqueo de caja y no descuentan stock.
/// Después se pueden pasar a Facturar para emitir la factura o la nota de pedido.
/// </summary>
public class PresupuestoService
{
    private readonly CatalogoService _catalogo;
    public PresupuestoService(CatalogoService catalogo) => _catalogo = catalogo;

    /// <param name="idExistente">null = presupuesto nuevo; con valor = modifica ese presupuesto (si sigue pendiente).</param>
    public async Task<(Guid Id, int Numero, string Error)> GuardarAsync(TenantConfig tenant, Guid usuarioId, NuevaFactura f, Guid? idExistente = null)
    {
        if (f.Renglones.Count == 0) return (Guid.Empty, 0, "El presupuesto no tiene renglones.");
        if (f.Renglones.Any(r => r.PrecioUnitario <= 0)) return (Guid.Empty, 0, "Hay renglones sin precio.");
        var errAjuste = FacturacionService.ValidarAjustes(f);
        if (errAjuste != null) return (Guid.Empty, 0, errAjuste);

        FormaPagoItem fp = f.FormaPagoId == Guid.Empty ? null : await _catalogo.FormaPagoAsync(tenant.ConnectionString, f.FormaPagoId);

        using var cn = await Db.AbrirAsync(tenant.ConnectionString);
        using var tx = cn.BeginTransaction();
        var id = idExistente ?? Guid.NewGuid();
        if (idExistente != null)
        {
            var estado = await cn.QueryFirstOrDefaultAsync<(bool? Cancelado, bool? Facturado)>(
                "SELECT Cancelado, Facturado FROM dbo.Presupuesto WITH (UPDLOCK) WHERE Id = @id", new { id }, tx);
            if (estado == default) return (Guid.Empty, 0, "El presupuesto no existe.");
            if (estado.Cancelado == true) return (Guid.Empty, 0, "El presupuesto está cancelado: no se puede modificar.");
            if (estado.Facturado == true) return (Guid.Empty, 0, "El presupuesto ya fue facturado: no se puede modificar.");
        }

        decimal total = 0, desc = 0, rec = 0;
        var detalles = new List<object>();
        int nro = 0;
        foreach (var r in f.Renglones)
        {
            nro++;
            var (monto, montoDesc, montoRec, _, _) = FacturacionService.CalcularRenglon(r, f);
            total += monto; desc += montoDesc; rec += montoRec;
            // DetallePresupuesto solo tiene DescuentoRenglon: el incremento del renglón se guarda en negativo.
            // El descuento/recargo sobre el total va en la cabecera (Descuento / Recargo).
            detalles.Add(new
            {
                Id = Guid.NewGuid(), Renglon = nro, Pu = r.PrecioUnitario, Cant = r.Cantidad,
                Desc = r.RecargoPorc > 0 ? -r.RecargoPorc : r.DescuentoPorc,
                Monto = monto, ProductoId = r.Producto.Id, PresupuestoId = id
            });
        }

        var cab = new
        {
            id, terminal = tenant.PuntoVenta, cant = f.Renglones.Count, total,
            descPorc = f.DescuentoTotalPorc, recPorc = f.RecargoTotalPorc,
            obs = PresupuestoCliente.Armar(f), fp = fp?.Id, cliente = f.ClienteId, usuario = usuarioId, desc, rec
        };
        if (idExistente != null)
        {
            await cn.ExecuteAsync(
                @"UPDATE dbo.Presupuesto SET CantidadRenglones = @cant, MontoTotal = @total, Descuento = @descPorc, Recargo = @recPorc,
                         Observaciones = @obs, FormaPagoId = @fp, ClienteId = @cliente, MontoDescuento = @desc, MontoRecargo = @rec
                  WHERE Id = @id", cab, tx);
            await cn.ExecuteAsync("DELETE FROM dbo.DetallePresupuesto WHERE PresupuestoId = @id", new { id }, tx);
        }
        else await cn.ExecuteAsync(
            @"INSERT INTO dbo.Presupuesto (Id, Fecha, Terminal, CantidadRenglones, MontoTotal, Descuento, Recargo,
                     Observaciones, FormaPagoId, ClienteId, UsuarioId, Cancelado, MontoDescuento, MontoRecargo, Despachado, Facturado)
              VALUES (@id, GETDATE(), @terminal, @cant, @total, @descPorc, @recPorc,
                     @obs, @fp, @cliente, @usuario, 0, @desc, @rec, 0, 0)", cab, tx);

        await cn.ExecuteAsync(
            @"INSERT INTO dbo.DetallePresupuesto (Id, Renglon, PU, CantidadRenglon, DescuentoRenglon, MontoRenglon, ProductoId, PresupuestoId)
              VALUES (@Id, @Renglon, @Pu, @Cant, @Desc, @Monto, @ProductoId, @PresupuestoId)", detalles, tx);

        // Numero es IDENTITY en la tabla (igual que en APC.WinForms): lo asigna SQL Server
        var numero = await cn.ExecuteScalarAsync<int>("SELECT Numero FROM dbo.Presupuesto WHERE Id = @id", new { id }, tx);

        tx.Commit();
        return (id, numero, null);
    }

    public async Task<List<PresupuestoListado>> ListarAsync(string cs, DateTime desde, DateTime hasta, Guid? usuarioId)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<PresupuestoListado>(
            @"SELECT TOP 500 p.Id, p.Fecha, p.Terminal, p.Numero, p.MontoTotal, p.Observaciones,
                     fp.Nombre AS FormaPago, u.NombreUsuario AS Usuario,
                     ISNULL(p.Cancelado, 0) AS Cancelado, ISNULL(p.Facturado, 0) AS Facturado
              FROM dbo.Presupuesto p
              LEFT JOIN dbo.FormaPago fp ON fp.Id = p.FormaPagoId
              LEFT JOIN dbo.Usuario u ON u.Id = p.UsuarioId
              WHERE p.Fecha >= @desde AND p.Fecha < @hasta
                AND (@usuarioId IS NULL OR p.UsuarioId = @usuarioId)
              ORDER BY p.Fecha DESC",
            new { desde = desde.Date, hasta = hasta.Date.AddDays(1), usuarioId })).ToList();
    }

    public async Task<PresupuestoImpresion> ObtenerAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        var p = await cn.QueryFirstOrDefaultAsync<PresupuestoImpresion>(
            @"SELECT p.Id, p.Fecha, p.Terminal, p.Numero, p.MontoTotal, p.Observaciones, p.FormaPagoId,
                     fp.Nombre AS FormaPago, u.NombreUsuario AS Usuario,
                     ISNULL(p.Cancelado, 0) AS Cancelado, ISNULL(p.Facturado, 0) AS Facturado,
                     ISNULL(p.MontoDescuento, 0) AS MontoDescuento, ISNULL(p.MontoRecargo, 0) AS MontoRecargo,
                     ISNULL(p.Descuento, 0) AS DescuentoTotalPorc, ISNULL(p.Recargo, 0) AS RecargoTotalPorc
              FROM dbo.Presupuesto p
              LEFT JOIN dbo.FormaPago fp ON fp.Id = p.FormaPagoId
              LEFT JOIN dbo.Usuario u ON u.Id = p.UsuarioId
              WHERE p.Id = @id", new { id });
        if (p == null) return null;
        p.Renglones = (await cn.QueryAsync<PresupuestoRenglon>(
            @"SELECT d.ProductoId, pr.Codigo, pr.Nombre AS Producto, d.Pu, d.CantidadRenglon,
                     ISNULL(d.DescuentoRenglon, 0) AS DescuentoRenglon, d.MontoRenglon
              FROM dbo.DetallePresupuesto d LEFT JOIN dbo.Producto pr ON pr.Id = d.ProductoId
              WHERE d.PresupuestoId = @id
              ORDER BY d.Renglon", new { id })).ToList();
        return p;
    }

    /// <summary>Arma el carrito de Facturar con los renglones del presupuesto (precios del presupuesto).</summary>
    public async Task<NuevaFactura> ACarritoAsync(string cs, Guid id)
    {
        var (f, p) = await CargarAsync(cs, id);
        if (f == null) return null;
        f.Observaciones = $"Presupuesto Nº {p.NumeroCompleto}";
        return f;
    }

    /// <summary>Lee un presupuesto para editarlo (o facturarlo): renglones con los precios guardados.</summary>
    public async Task<(NuevaFactura Datos, PresupuestoImpresion Presupuesto)> CargarAsync(string cs, Guid id)
    {
        var p = await ObtenerAsync(cs, id);
        if (p == null) return (null, null);
        var ids = p.Renglones.Select(r => r.ProductoId).Distinct().ToList();
        var productos = (await _catalogo.ProductosPorIdAsync(cs, ids)).ToDictionary(x => x.Id);
        var cli = p.Cliente;
        var f = new NuevaFactura
        {
            ClienteNombre = cli.Nombre, ClienteDocumento = cli.Documento,
            ClienteDomicilio = cli.Domicilio, ClienteLocalidad = cli.Localidad,
            Observaciones = cli.Observaciones
        };
        foreach (var r in p.Renglones)
        {
            if (!productos.TryGetValue(r.ProductoId, out var prod)) continue;
            f.Renglones.Add(new RenglonCarrito
            {
                Producto = prod, Cantidad = r.CantidadRenglon, PrecioUnitario = r.Pu,
                DescuentoPorc = r.DescuentoRenglon > 0 ? r.DescuentoRenglon : 0,
                RecargoPorc = r.DescuentoRenglon < 0 ? -r.DescuentoRenglon : 0
            });
        }
        f.DescuentoTotalPorc = p.DescuentoTotalPorc;
        f.RecargoTotalPorc = p.RecargoTotalPorc;
        if (p.FormaPagoId != null) f.FormaPagoId = p.FormaPagoId.Value;
        return (f, p);
    }

    public async Task MarcarFacturadoAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        await cn.ExecuteAsync("UPDATE dbo.Presupuesto SET Facturado = 1 WHERE Id = @id", new { id });
    }

    public async Task CancelarAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        await cn.ExecuteAsync("UPDATE dbo.Presupuesto SET Cancelado = 1 WHERE Id = @id", new { id });
    }
}

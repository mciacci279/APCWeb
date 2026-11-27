using System.Data;
using System.Text;
using System.Text.Json;
using APC.Arca;
using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Tenants;
using Dapper;
using QRCoder;

namespace APC.Core.Servicios;

/// <summary>
/// Emisión de facturas. Es la lógica de frmFacturaEdicion.btnAgregar_Click + GuardarImprimir()
/// de APC.WinForms, sin interfaz: mismos cálculos de IVA (precios con IVA incluido),
/// mismas tablas (Factura / Detalle) y el mismo cliente de ARCA.
/// </summary>
public class FacturacionService
{
    private const string UrlQrArca = "https://www.afip.gob.ar/fe/qr/?p=";
    private readonly CatalogoService _catalogo;
    private readonly ArcaService _arca;
    private readonly TenantRegistry _tenants;

    public FacturacionService(CatalogoService catalogo, ArcaService arca, TenantRegistry tenants)
    {
        _catalogo = catalogo;
        _arca = arca;
        _tenants = tenants;
    }

    // ------------------------------------------------------------------ cálculo

    /// <summary>Alícuota que corresponde al producto (misma regla que APC).</summary>
    public static decimal AlicuotaProducto(ProductoItem p, decimal alicuotaGeneral, decimal alicuotaDiferenciada)
    {
        if (p.IvaExento) return 0m;
        if (p.IvaDiferenciado) return alicuotaDiferenciada;
        return alicuotaGeneral;
    }

    /// <summary>
    /// Importe final de un renglón, igual que APC.WinForms:
    /// 1) descuento o recargo propio del renglón sobre precio × cantidad;
    /// 2) descuento y recargo sobre el total (ActualizarRenglonesDescuentoRecargo), aplicados a cada renglón.
    /// Los porcentajes que se guardan son los del renglón si los tiene; si no, los del total.
    /// </summary>
    public static (decimal Monto, decimal MontoDescuento, decimal MontoRecargo, decimal DescuentoPorc, decimal RecargoPorc) CalcularRenglon(
        RenglonCarrito r, decimal descuentoTotalPorc, decimal recargoTotalPorc)
    {
        decimal bruto = r.PrecioUnitario * r.Cantidad;
        decimal descLinea = bruto * (r.DescuentoPorc / 100m);
        decimal recLinea = bruto * (r.RecargoPorc / 100m);
        decimal parcial = bruto - descLinea + recLinea;
        decimal descTotal = parcial * (descuentoTotalPorc / 100m);
        decimal recTotal = parcial * (recargoTotalPorc / 100m);
        decimal monto = parcial - descTotal + recTotal;
        return (Math.Round(monto, 2), Math.Round(descLinea + descTotal, 2), Math.Round(recLinea + recTotal, 2),
                r.DescuentoPorc > 0 ? r.DescuentoPorc : descuentoTotalPorc,
                r.RecargoPorc > 0 ? r.RecargoPorc : recargoTotalPorc);
    }

    public static (decimal Monto, decimal MontoDescuento, decimal MontoRecargo, decimal DescuentoPorc, decimal RecargoPorc) CalcularRenglon(
        RenglonCarrito r, NuevaFactura f) => CalcularRenglon(r, f.DescuentoTotalPorc, f.RecargoTotalPorc);

    /// <summary>Totales para mostrar en pantalla mientras se arma la factura.</summary>
    public static TotalesFactura CalcularTotales(NuevaFactura f, decimal alicuotaGeneral, decimal alicuotaDiferenciada, bool esMonotributo)
    {
        var t = new TotalesFactura();
        foreach (var r in f.Renglones)
        {
            var c = CalcularRenglon(r, f);
            var monto = c.Monto;
            t.Total += monto;
            t.MontoDescuento += c.MontoDescuento;
            t.MontoRecargo += c.MontoRecargo;
            if (esMonotributo) continue;
            var alic = AlicuotaProducto(r.Producto, alicuotaGeneral, alicuotaDiferenciada);
            if (alic == 0m) t.NoGravado += monto;
            else t.Iva += Math.Round(monto - monto / (1 + alic / 100m), 2);
        }
        return t;
    }

    /// <summary>Valida los porcentajes cargados a mano. Devuelve el error o null.</summary>
    public static string ValidarAjustes(NuevaFactura f)
    {
        if (f.DescuentoTotalPorc < 0 || f.DescuentoTotalPorc >= 100) return "El descuento sobre el total debe estar entre 0 y 99,99 %.";
        if (f.RecargoTotalPorc < 0 || f.RecargoTotalPorc > 1000) return "El recargo sobre el total debe estar entre 0 y 1000 %.";
        if (f.Renglones.Any(r => r.DescuentoPorc < 0 || r.DescuentoPorc >= 100)) return "El descuento de un producto debe estar entre 0 y 99,99 %.";
        if (f.Renglones.Any(r => r.RecargoPorc < 0 || r.RecargoPorc > 1000)) return "El incremento de un producto debe estar entre 0 y 1000 %.";
        return null;
    }

    public class ParametrosIva
    {
        public decimal General { get; set; } = 21m;
        public decimal Diferenciada { get; set; } = 10.5m;
        public bool DecrementaStock { get; set; }
    }

    public async Task<ParametrosIva> ParametrosAsync(string cs)
    {
        var cfg = await _catalogo.ConfiguracionAsync(cs);
        var p = new ParametrosIva();
        if (cfg.TryGetValue("PROD_IVA_DIF", out var dif) && dif.Numero > 0d) p.Diferenciada = (decimal)dif.Numero.Value;
        // En la web el stock se controla por producto: siempre se descuenta al facturar (factura o nota de pedido)
        // si el producto no tiene marcado "No controlar stock", y se devuelve al anular. No se mira FACT_DECREMENTA
        // del sistema de escritorio, que en muchas bases quedó apagado y hacía que el stock no bajara nunca.
        p.DecrementaStock = true;
        return p;
    }

    // ------------------------------------------------------------------ emisión

    /// <param name="fiscal">false = nota de pedido: se guarda igual que la factura pero sin pedir CAE
    /// (como el "presupuesto" no fiscal de APC: Factura con Impresa = 1 y sin CAE).</param>
    public async Task<ResultadoEmision> EmitirAsync(TenantConfig tenant, Guid usuarioId, NuevaFactura f,
        bool fiscal = true, Sucursal sucursal = null)
    {
        // El punto de venta es el de la sucursal donde trabaja el usuario.
        int puntoVenta = sucursal?.PuntoVenta ?? tenant.PuntoVenta;
        if (f.Renglones.Count == 0) return Falla("La factura no tiene renglones.");
        if (f.FormaPagoId == Guid.Empty) return Falla("Elegí la forma de pago.");
        var errAjuste = ValidarAjustes(f);
        if (errAjuste != null) return Falla(errAjuste);

        var empresa = await _catalogo.EmpresaAsync(tenant.ConnectionString);
        if (empresa == null) return Falla("No hay datos de empresa cargados.");
        var param = await ParametrosAsync(tenant.ConnectionString);

        var doc = NormalizarDocumento(f.ClienteDocumento);
        if (!empresa.EsMonotributo && f.Condicion == CondicionReceptor.ResponsableInscripto && doc.Length != 11)
            return Falla("Para Factura A el cliente debe tener CUIT (11 dígitos).");

        Guid facturaId;
        using (var cn = await Db.AbrirAsync(tenant.ConnectionString))
        {
            var iva = await IvaCondicionAsync(cn, f.Condicion, null);
            if (iva == null) return Falla("No se encontró la condición de IVA en la tabla Iva.");
            decimal alicGeneral = iva.Porcentaje > 0 ? iva.Porcentaje : param.General;

            var fp = await _catalogo.FormaPagoAsync(tenant.ConnectionString, f.FormaPagoId);
            if (fp == null) return Falla("La forma de pago no existe.");
            if (fp.EsCtaCte) return Falla("La cuenta corriente no está disponible en la versión web.");

            string letra = empresa.EsMonotributo ? "C" : (string.IsNullOrWhiteSpace(iva.LetraComprobante) ? "B" : iva.LetraComprobante.Trim());

            using var tx = cn.BeginTransaction();
            facturaId = Guid.NewGuid();

            // Renglones: mismo cálculo que btnAgregar_Click
            var detalles = new List<object>();
            decimal total = 0, totalIva = 0, totalNoGravado = 0, totalDescuento = 0, totalRecargo = 0;
            int nro = 0;
            foreach (var r in f.Renglones)
            {
                nro++;
                var (monto, montoDesc, montoRec, descPorc, recPorc) = CalcularRenglon(r, f);

                decimal alic = empresa.EsMonotributo ? 0m : AlicuotaProducto(r.Producto, alicGeneral, param.Diferenciada);
                decimal montoIva = alic == 0m ? 0m : Math.Round(monto - monto / (1 + alic / 100m), 2);
                decimal? noGravado = (!empresa.EsMonotributo && r.Producto.IvaExento) ? monto : null;

                total += monto;
                totalIva += montoIva;
                totalNoGravado += noGravado ?? 0;
                totalDescuento += montoDesc;
                totalRecargo += montoRec;

                detalles.Add(new
                {
                    Id = Guid.NewGuid(), Renglon = nro, PU = r.PrecioUnitario, Cant = r.Cantidad,
                    Desc = descPorc, MDesc = Math.Round(montoDesc, 2),
                    Rec = recPorc, MRec = Math.Round(montoRec, 2),
                    Monto = monto, MIva = montoIva, Iva = alic, ProductoId = r.Producto.Id,
                    FacturaId = facturaId, NoGrav = noGravado
                });
            }

            await cn.ExecuteAsync(
                @"INSERT INTO dbo.Factura
                    (Id, Fecha, Tipo, MontoTotal, MontoIva, Iva, MontoNoGravado, MontoExento,
                     Descuento, MontoDescuento, Recargo, MontoRecargo, Impresa, Anulada, Observaciones,
                     IvaId, FormaPagoId, ClienteId, UsuarioId, ClienteNombre, ClienteCUIT,
                     ClienteDomicilio, ClienteLocalidad, Terminal, CantidadRenglones, ClienteTipoDocAFIP)
                  VALUES
                    (@Id, GETDATE(), @Tipo, @Total, @Iva, @Alic, @NoGrav, 0,
                     @Desc, @MDesc, @Rec, @MRec, 0, 0, @Obs,
                     @IvaId, @FormaPagoId, @ClienteId, @UsuarioId, @ClienteNombre, @ClienteCuit,
                     @ClienteDomicilio, @ClienteLocalidad, @Terminal, @Renglones, @TipoDocArca)",
                new
                {
                    Id = facturaId, Tipo = letra, Total = total, Iva = totalIva, Alic = alicGeneral,
                    NoGrav = totalNoGravado, Desc = f.DescuentoTotalPorc, MDesc = Math.Round(totalDescuento, 2),
                    Rec = f.RecargoTotalPorc, MRec = Math.Round(totalRecargo, 2),
                    Obs = f.Observaciones, IvaId = iva.Id, FormaPagoId = fp.Id, f.ClienteId,
                    UsuarioId = usuarioId, ClienteNombre = Recortar(f.ClienteNombre, 50),
                    ClienteCuit = Recortar(f.ClienteDocumento?.Trim(), 13),
                    TipoDocArca = CodigoTipoDocArca(f.ClienteTipoDocumento),
                    ClienteDomicilio = Recortar(f.ClienteDomicilio, 100),
                    ClienteLocalidad = Recortar(f.ClienteLocalidad, 100),
                    Terminal = puntoVenta, Renglones = f.Renglones.Count
                }, tx);

            await cn.ExecuteAsync(
                @"INSERT INTO dbo.Detalle
                    (Id, Renglon, PU, CantidadRenglon, DescuentoRenglon, MontoDescuentoRenglon,
                     RecargoRenglon, MontoRecargoRenglon, MontoRenglon, MontoIvaRenglon, IvaRenglon,
                     ProductoId, FacturaId, MontoNoGravadoRenglon)
                  VALUES
                    (@Id, @Renglon, @PU, @Cant, @Desc, @MDesc, @Rec, @MRec, @Monto, @MIva, @Iva,
                     @ProductoId, @FacturaId, @NoGrav)", detalles, tx);

            if (param.DecrementaStock)
            {
                foreach (var g in f.Renglones.Where(r => !r.Producto.OmitirControlStock).GroupBy(r => r.Producto.Id))
                {
                    var cantidad = g.Sum(x => x.Cantidad);
                    if (cantidad <= 0) continue;
                    // El stock nunca queda negativo: si había menos de lo vendido, queda en 0.
                    if (sucursal != null)
                        await SucursalService.DescontarVentaAsync(cn, tx, sucursal.Id, g.Key, cantidad);
                    else
                        await cn.ExecuteAsync(
                            @"UPDATE dbo.Producto
                              SET Stock = CASE WHEN ISNULL(Stock, 0) - @c <= 0 THEN 0 ELSE Stock - @c END
                              WHERE Id = @id AND ISNULL(OmitirControlStock, 0) = 0", new { id = g.Key, c = cantidad }, tx);
                }
            }

            tx.Commit();
        }

        return fiscal ? await SolicitarCaeAsync(tenant, facturaId) : await CerrarNotaPedidoAsync(tenant, facturaId, puntoVenta);
    }

    // ------------------------------------------------------------------ notas de pedido (no fiscales)

    private static async Task<ResultadoEmision> CerrarNotaPedidoAsync(TenantConfig tenant, Guid facturaId, int puntoVenta)
    {
        using var cn = await Db.AbrirAsync(tenant.ConnectionString);
        var numero = await cn.ExecuteScalarAsync<int>(
            @"UPDATE dbo.Factura SET Impresa = 1, FechaImpresion = GETDATE() WHERE Id = @id;
              SELECT NumeroInterno FROM dbo.Factura WHERE Id = @id", new { id = facturaId });
        return new ResultadoEmision
        {
            Ok = true, FacturaId = facturaId, Letra = "X", PuntoVenta = puntoVenta, Numero = numero, EsNotaPedido = true
        };
    }

    /// <summary>Anula una nota de pedido (no fiscal): la marca anulada y devuelve el stock. No va a ARCA.</summary>
    public async Task<ResultadoEmision> AnularNotaPedidoAsync(TenantConfig tenant, Guid facturaId, string motivo)
    {
        var param = await ParametrosAsync(tenant.ConnectionString);
        using var cn = await Db.AbrirAsync(tenant.ConnectionString);
        var fac = await cn.QueryFirstOrDefaultAsync<(string CAE, bool? Impresa, bool? Anulada, string Observaciones)>(
            "SELECT CAE, Impresa, Anulada, Observaciones FROM dbo.Factura WHERE Id = @id", new { id = facturaId });
        if (fac.Impresa != true || !string.IsNullOrEmpty(fac.CAE)) return Falla("El comprobante no es una nota de pedido.");
        if (fac.Anulada == true) return Falla("La nota de pedido ya estaba anulada.");

        using var tx = cn.BeginTransaction();
        var obs = Recortar(string.Join(" | ", new[] { fac.Observaciones, "Anulada: " + (motivo ?? "").Trim() }
            .Where(x => !string.IsNullOrWhiteSpace(x))), 4000);
        await cn.ExecuteAsync(
            "UPDATE dbo.Factura SET Anulada = 1, FechaAnulacion = GETDATE(), Observaciones = @obs WHERE Id = @id",
            new { id = facturaId, obs }, tx);
        if (param.DecrementaStock) await DevolverStockAsync(cn, tx, facturaId);
        tx.Commit();
        return new ResultadoEmision { Ok = true, FacturaId = facturaId, Letra = "X" };
    }

    /// <summary>
    /// Pide el CAE para una factura ya guardada (se usa al emitir y para reintentar
    /// una factura que quedó sin CAE porque ARCA no respondió).
    /// </summary>
    public async Task<ResultadoEmision> SolicitarCaeAsync(TenantConfig tenant, Guid facturaId)
    {
        var empresa = await _catalogo.EmpresaAsync(tenant.ConnectionString);
        using var cn = await Db.AbrirAsync(tenant.ConnectionString);

        var fac = await cn.QueryFirstOrDefaultAsync<FacturaCab>(
            @"SELECT Id, Fecha, Tipo, MontoTotal, IvaId, ClienteCUIT, CAE, Terminal, Numero, ISNULL(Impresa, 0) AS Impresa,
                     TRY_CAST(ClienteTipoDocAFIP AS INT) AS ClienteTipoDocAFIP
              FROM dbo.Factura WHERE Id = @id", new { id = facturaId });
        if (fac == null) return Falla("La factura no existe.");
        if (fac.Impresa && string.IsNullOrEmpty(fac.CAE)) return Falla("Es una nota de pedido: no lleva CAE.");
        if (!string.IsNullOrEmpty(fac.CAE))
            return new ResultadoEmision { Ok = true, FacturaId = fac.Id, Letra = fac.Tipo, PuntoVenta = fac.Terminal, Numero = fac.Numero ?? 0, Cae = fac.CAE };

        var renglones = (await cn.QueryAsync<DetalleCalc>(
            @"SELECT MontoRenglon, ISNULL(MontoIvaRenglon,0) AS MontoIvaRenglon, ISNULL(IvaRenglon,0) AS IvaRenglon,
                     ISNULL(MontoNoGravadoRenglon,0) AS MontoNoGravadoRenglon
              FROM dbo.Detalle WHERE FacturaId = @id", new { id = facturaId })).ToList();

        var iva = await IvaCondicionAsync(cn, null, fac.IvaId);
        var condicion = iva?.Condicion ?? CondicionReceptor.ConsumidorFinal;
        int tipoComp = empresa.EsMonotributo ? 11 : condicion == CondicionReceptor.ResponsableInscripto ? 1 : 6;

        var r = await PedirCaeAsync(tenant, cn, empresa, tipoComp, condicion, fac.ClienteCUIT, fac.MontoTotal, renglones, null, fac.Terminal,
            fac.ClienteTipoDocAFIP);
        if (!r.Ok)
            return new ResultadoEmision { Ok = false, FacturaId = facturaId, Letra = fac.Tipo, PuntoVenta = fac.Terminal, Mensaje = r.Mensaje };

        await cn.ExecuteAsync(
            @"UPDATE dbo.Factura SET
                Terminal = @pv, Numero = @nro, TipoComprobanteAFIP = @tipo, Impresa = 1, FechaImpresion = GETDATE(),
                CAE = @cae, CAEVencimiento = @vto, ClienteTipoDocAFIP = @tipoDoc,
                QRParametros = @qrJson, QRURLAFIP = @qrUrl, QRCode = @qrPng,
                MontoIvaTipoAfip4 = @i4, MontoIvaTipoAfip5 = @i5, MontoIvaTipoAfip6 = @i6
              WHERE Id = @id",
            new
            {
                id = facturaId, pv = fac.Terminal, nro = (int)r.Numero, tipo = tipoComp,
                cae = r.Cae, vto = r.CaeVencimiento, tipoDoc = r.TipoDoc, qrJson = r.QrJson, qrUrl = r.QrUrl,
                qrPng = r.QrPng, i4 = r.Iva4, i5 = r.Iva5, i6 = r.Iva6
            });

        return new ResultadoEmision
        {
            Ok = true, FacturaId = facturaId, Letra = fac.Tipo, PuntoVenta = fac.Terminal,
            Numero = r.Numero, Cae = r.Cae
        };
    }

    // ------------------------------------------------------------------ notas de crédito

    /// <summary>
    /// Anula una factura con CAE emitiendo una nota de crédito por el total, con los mismos datos y renglones.
    /// Usa las tablas de APC (NotaCredito / DetalleNotaCredito), vinculada por letra, punto de venta y número.
    /// Si la factura ya tiene una nota de crédito sin CAE (ARCA no respondió), reintenta esa en lugar de crear otra.
    /// </summary>
    public async Task<ResultadoEmision> AnularConNotaCreditoAsync(TenantConfig tenant, Guid usuarioId, Guid facturaId, string motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo)) return Falla("Indicá el motivo de la anulación.");
        var param = await ParametrosAsync(tenant.ConnectionString);
        Guid ncId;

        using (var cn = await Db.AbrirAsync(tenant.ConnectionString))
        {
            var fac = await cn.QueryFirstOrDefaultAsync<FacturaCompleta>(
                "SELECT * FROM dbo.Factura WHERE Id = @id", new { id = facturaId });
            if (fac == null) return Falla("La factura no existe.");
            if (string.IsNullOrEmpty(fac.CAE) || fac.Numero == null || fac.TipoComprobanteAFIP == null)
                return Falla("La factura no tiene CAE: no hace falta nota de crédito. Pedí el CAE o dejala sin emitir.");

            var existente = await NotaCreditoDeFacturaAsync(cn, fac.Tipo, fac.Terminal, fac.Numero.Value);
            if (existente != null)
            {
                if (!string.IsNullOrEmpty(existente.CAE))
                    return Falla($"La factura ya fue anulada con la nota de crédito {existente.Tipo} {existente.Terminal:00000}-{(existente.Numero ?? 0):00000000}.");
                ncId = existente.Id; // quedó pendiente de CAE: se reintenta
            }
            else
            {
                using var tx = cn.BeginTransaction();
                ncId = Guid.NewGuid();
                var obs = Recortar($"Anula factura {fac.Tipo} {fac.Terminal:00000}-{fac.Numero:00000000}. Motivo: {motivo.Trim()}", 4000);

                await cn.ExecuteAsync(
                    @"INSERT INTO dbo.NotaCredito
                        (Id, Fecha, Tipo, Terminal, CantidadRenglones, MontoTotal, MontoIva, Iva, MontoNoGravado, MontoExento,
                         Impresa, Anulada, Observaciones, IvaId, FormaPagoId, ClienteId, UsuarioId, ClienteNombre, ClienteCUIT,
                         ClienteDomicilio, ClienteLocalidad, NroFacturaRelacionada, TipoFacturaRelacionada, TerminalFacturaRelacionada)
                      VALUES
                        (@Id, GETDATE(), @Tipo, @Terminal, @CantidadRenglones, @MontoTotal, @MontoIva, @Iva, @MontoNoGravado, @MontoExento,
                         0, 0, @Obs, @IvaId, @FormaPagoId, @ClienteId, @UsuarioId, @ClienteNombre, @ClienteCUIT,
                         @ClienteDomicilio, @ClienteLocalidad, @NroFac, @TipoFac, @TerminalFac)",
                    new
                    {
                        Id = ncId, fac.Tipo, Terminal = fac.Terminal, fac.CantidadRenglones, fac.MontoTotal, fac.MontoIva, fac.Iva,
                        fac.MontoNoGravado, fac.MontoExento, Obs = obs, fac.IvaId, fac.FormaPagoId, fac.ClienteId, UsuarioId = usuarioId,
                        fac.ClienteNombre, fac.ClienteCUIT, fac.ClienteDomicilio, fac.ClienteLocalidad,
                        NroFac = fac.Numero, TipoFac = fac.Tipo, TerminalFac = fac.Terminal
                    }, tx);

                await cn.ExecuteAsync(
                    @"INSERT INTO dbo.DetalleNotaCredito
                        (Id, Renglon, PU, CantidadRenglon, DescuentoRenglon, MontoRenglon, ProductoId, NotaCreditoId,
                         MontoIvaRenglon, IvaRenglon, MontoNoGravadoRenglon, MontoExentoRenglon)
                      SELECT NEWID(), Renglon, PU, CantidadRenglon, DescuentoRenglon, MontoRenglon, ProductoId, @nc,
                             MontoIvaRenglon, IvaRenglon, MontoNoGravadoRenglon, MontoExentoRenglon
                      FROM dbo.Detalle WHERE FacturaId = @fac",
                    new { nc = ncId, fac = facturaId }, tx);

                // Devuelve al stock lo que la factura había descontado
                if (param.DecrementaStock)
                    await DevolverStockAsync(cn, tx, facturaId);
                tx.Commit();
            }
        }

        return await SolicitarCaeNotaCreditoAsync(tenant, ncId);
    }

    public async Task<ResultadoEmision> SolicitarCaeNotaCreditoAsync(TenantConfig tenant, Guid notaCreditoId)
    {
        var empresa = await _catalogo.EmpresaAsync(tenant.ConnectionString);
        using var cn = await Db.AbrirAsync(tenant.ConnectionString);

        var nc = await cn.QueryFirstOrDefaultAsync<NotaCreditoCab>(
            @"SELECT Id, Tipo, Terminal, Numero, MontoTotal, IvaId, ClienteCUIT, CAE,
                     NroFacturaRelacionada, TipoFacturaRelacionada, TerminalFacturaRelacionada
              FROM dbo.NotaCredito WHERE Id = @id", new { id = notaCreditoId });
        if (nc == null) return Falla("La nota de crédito no existe.");
        if (!string.IsNullOrEmpty(nc.CAE))
            return new ResultadoEmision { Ok = true, FacturaId = nc.Id, Letra = nc.Tipo, PuntoVenta = nc.Terminal, Numero = nc.Numero ?? 0, Cae = nc.CAE };

        var fac = await cn.QueryFirstOrDefaultAsync<FacturaCompleta>(
            @"SELECT TOP 1 * FROM dbo.Factura
              WHERE Tipo = @t AND Terminal = @pv AND Numero = @n AND CAE IS NOT NULL",
            new { t = nc.TipoFacturaRelacionada, pv = nc.TerminalFacturaRelacionada, n = nc.NroFacturaRelacionada });
        if (fac == null) return Falla("No se encontró la factura asociada a la nota de crédito.");

        var renglones = (await cn.QueryAsync<DetalleCalc>(
            @"SELECT MontoRenglon, ISNULL(MontoIvaRenglon,0) AS MontoIvaRenglon, ISNULL(IvaRenglon,0) AS IvaRenglon,
                     ISNULL(MontoNoGravadoRenglon,0) AS MontoNoGravadoRenglon
              FROM dbo.DetalleNotaCredito WHERE NotaCreditoId = @id", new { id = notaCreditoId })).ToList();

        // Nota de crédito A = 3, B = 8, C = 13 (según la factura que anula)
        int tipoComp = fac.TipoComprobanteAFIP switch { 1 => 3, 11 => 13, _ => 8 };
        var iva = await IvaCondicionAsync(cn, null, nc.IvaId);
        var condicion = iva?.Condicion ?? CondicionReceptor.ConsumidorFinal;
        var asociado = new ComprobanteAsociado
        {
            Tipo = fac.TipoComprobanteAFIP.Value,
            PuntoVenta = fac.Terminal,
            Numero = fac.Numero.Value,
            Cuit = empresa.CuitNumero,
            Fecha = fac.Fecha
        };

        var r = await PedirCaeAsync(tenant, cn, empresa, tipoComp, condicion, nc.ClienteCUIT, nc.MontoTotal, renglones, asociado, nc.Terminal);
        if (!r.Ok)
            return new ResultadoEmision { Ok = false, FacturaId = notaCreditoId, Letra = nc.Tipo, PuntoVenta = nc.Terminal, Mensaje = r.Mensaje };

        await cn.ExecuteAsync(
            @"UPDATE dbo.NotaCredito SET
                Terminal = @pv, Numero = @nro, TipoComprobanteAFIP = @tipo, Impresa = 1, FechaImpresion = GETDATE(),
                CAE = @cae, CAEVencimiento = @vto, ClienteTipoDocAFIP = @tipoDoc,
                QRParametros = @qrJson, QRURLAFIP = @qrUrl, QRCode = @qrPng,
                MontoIvaTipoAfip4 = @i4, MontoIvaTipoAfip5 = @i5, MontoIvaTipoAfip6 = @i6
              WHERE Id = @id",
            new
            {
                id = notaCreditoId, pv = nc.Terminal, nro = (int)r.Numero, tipo = tipoComp,
                cae = r.Cae, vto = r.CaeVencimiento, tipoDoc = r.TipoDoc, qrJson = r.QrJson, qrUrl = r.QrUrl,
                qrPng = r.QrPng, i4 = r.Iva4, i5 = r.Iva5, i6 = r.Iva6
            });

        return new ResultadoEmision
        {
            Ok = true, FacturaId = notaCreditoId, Letra = nc.Tipo, PuntoVenta = nc.Terminal, Numero = r.Numero, Cae = r.Cae
        };
    }

    private static Task<NotaCreditoCab> NotaCreditoDeFacturaAsync(IDbConnection cn, string tipo, int terminal, int numero) =>
        cn.QueryFirstOrDefaultAsync<NotaCreditoCab>(
            @"SELECT TOP 1 Id, Tipo, Terminal, Numero, MontoTotal, IvaId, ClienteCUIT, CAE,
                     NroFacturaRelacionada, TipoFacturaRelacionada, TerminalFacturaRelacionada
              FROM dbo.NotaCredito
              WHERE TipoFacturaRelacionada = @tipo AND TerminalFacturaRelacionada = @terminal AND NroFacturaRelacionada = @numero
                AND ISNULL(Anulada, 0) = 0
              ORDER BY Fecha DESC",
            new { tipo, terminal, numero });

    // ------------------------------------------------------------------ pedido de CAE (común)

    private class CaeObtenido
    {
        public bool Ok { get; set; }
        public string Mensaje { get; set; }
        public long Numero { get; set; }
        public string Cae { get; set; }
        public string CaeVencimiento { get; set; }
        public int TipoDoc { get; set; }
        public string QrJson { get; set; }
        public string QrUrl { get; set; }
        public byte[] QrPng { get; set; }
        public decimal Iva4 { get; set; }
        public decimal Iva5 { get; set; }
        public decimal Iva6 { get; set; }
    }

    /// <summary>Arma el pedido (alícuotas, receptor, asociado), lo envía a ARCA y prepara el QR.</summary>

    /// <summary>
    /// Devuelve al stock lo que una factura había descontado. Si la empresa trabaja con sucursales,
    /// la mercadería vuelve a la sucursal del punto de venta del comprobante.
    /// </summary>
    private static async Task DevolverStockAsync(IDbConnection cn, IDbTransaction tx, Guid facturaId)
    {
        await cn.ExecuteAsync(
            @"UPDATE p SET p.Stock = ISNULL(p.Stock, 0) + d.Cant
              FROM dbo.Producto p
              JOIN (SELECT ProductoId, SUM(CantidadRenglon) AS Cant FROM dbo.Detalle WHERE FacturaId = @id GROUP BY ProductoId) d
                ON d.ProductoId = p.Id
              WHERE ISNULL(p.OmitirControlStock, 0) = 0;

              IF OBJECT_ID(N'dbo.StockSucursal', N'U') IS NOT NULL
              UPDATE ss SET ss.Stock = ss.Stock + d.Cant
              FROM dbo.StockSucursal ss
              JOIN dbo.Factura f ON f.Id = @id
              JOIN dbo.Sucursal s ON s.PuntoVenta = f.Terminal AND s.Id = ss.SucursalId
              JOIN (SELECT ProductoId, SUM(CantidadRenglon) AS Cant FROM dbo.Detalle WHERE FacturaId = @id GROUP BY ProductoId) d
                ON d.ProductoId = ss.ProductoId
              JOIN dbo.Producto p ON p.Id = ss.ProductoId AND ISNULL(p.OmitirControlStock, 0) = 0;",
            new { id = facturaId }, tx);
    }

    private async Task<CaeObtenido> PedirCaeAsync(TenantConfig tenant, IDbConnection cn, EmpresaDatos empresa, int tipoComp,
        CondicionReceptor condicion, string clienteCuit, decimal montoTotal, List<DetalleCalc> renglones,
        ComprobanteAsociado asociado, int puntoVenta, int? tipoDocElegido = null)
    {
        var docNum = NormalizarDocumento(clienteCuit);
        int tipoDoc = docNum.Length == 11 ? 80 : (docNum.Length is 7 or 8 ? 96 : 99);
        // Con 11 dígitos puede ser CUIL o CDI en vez de CUIT, si así se eligió en la factura.
        if (docNum.Length == 11 && tipoDocElegido is 86 or 87) tipoDoc = tipoDocElegido.Value;
        long nroDoc = tipoDoc == 99 ? 0 : long.Parse(docNum);

        var sol = new SolicitudCae
        {
            TipoComprobante = tipoComp,
            TipoDocReceptor = tipoDoc,
            NroDocReceptor = nroDoc,
            CondicionIvaReceptorId = CondicionArca(condicion),
            Fecha = DateTime.Today,
            Asociado = asociado
        };

        var res = new CaeObtenido { TipoDoc = tipoDoc };
        bool sinIva = tipoComp == 11 || tipoComp == 13;
        if (sinIva)
        {
            sol.ImporteTotal = Math.Round(montoTotal, 2);
            sol.ImporteNeto = sol.ImporteTotal;
        }
        else
        {
            // Agrupa por alícuota y arma totales que cierran exacto (ARCA valida las sumas)
            var codigosAlicuota = await CodigosAlicuotaAsync(cn);
            foreach (var g in renglones.Where(r => r.IvaRenglon > 0).GroupBy(r => r.IvaRenglon))
            {
                var importe = Math.Round(g.Sum(x => x.MontoIvaRenglon), 2);
                var baseImp = Math.Round(g.Sum(x => x.MontoRenglon) - importe, 2);
                int codigo = CodigoAlicuota(g.Key, codigosAlicuota);
                sol.Alicuotas.Add(new AlicuotaIva { Id = codigo, BaseImponible = baseImp, Importe = importe });
                if (codigo == 4) res.Iva4 += importe; else if (codigo == 5) res.Iva5 += importe; else if (codigo == 6) res.Iva6 += importe;
            }
            sol.ImporteNoGravado = Math.Round(renglones.Sum(r => r.MontoNoGravadoRenglon), 2);
            sol.ImporteNeto = sol.Alicuotas.Sum(a => a.BaseImponible);
            sol.ImporteTotal = sol.ImporteNeto + sol.Alicuotas.Sum(a => a.Importe) + sol.ImporteNoGravado;
        }

        var cfg = new ArcaConfig
        {
            Cuit = empresa.CuitNumero,
            PuntoVenta = puntoVenta,
            CertificadoPath = tenant.CertificadoPath,
            ClavePath = tenant.ClavePath,
            UrlWsaa = tenant.UrlWsaa,
            UrlWsfe = tenant.UrlWsfe,
            Concepto = tenant.Concepto,
            Moneda = tenant.Moneda,
            CarpetaCacheTA = Path.Combine(_tenants.CarpetaDatos, "ta", empresa.CuitNumero + "_" + tenant.Ambiente)
        };

        var resp = await Task.Run(() => _arca.SolicitarCae(cfg, sol));
        if (!resp.Aprobado) { res.Mensaje = resp.Mensaje; return res; }

        res.Ok = true;
        res.Numero = resp.Numero;
        res.Cae = resp.Cae;
        res.CaeVencimiento = resp.CaeVencimiento;
        res.QrJson = JsonSerializer.Serialize(new
        {
            ver = 1,
            fecha = sol.Fecha.ToString("yyyy-MM-dd"),
            cuit = empresa.CuitNumero,
            ptoVta = puntoVenta,
            tipoCmp = tipoComp,
            nroCmp = resp.Numero,
            importe = sol.ImporteTotal,
            moneda = tenant.Moneda,
            ctz = 1,
            tipoDocRec = tipoDoc,
            nroDocRec = nroDoc,
            tipoCodAut = "E",
            codAut = long.Parse(resp.Cae)
        });
        res.QrUrl = UrlQrArca + Convert.ToBase64String(Encoding.UTF8.GetBytes(res.QrJson));
        res.QrPng = QrPng(res.QrUrl);
        return res;
    }

    // ------------------------------------------------------------------ consultas

    public async Task<ComprobanteImpresion> ObtenerParaImprimirAsync(string cs, Guid id, bool esNotaCredito = false)
    {
        string tabla = esNotaCredito ? "dbo.NotaCredito" : "dbo.Factura";
        string detalle = esNotaCredito ? "dbo.DetalleNotaCredito" : "dbo.Detalle";
        string fk = esNotaCredito ? "NotaCreditoId" : "FacturaId";
        string asociado = esNotaCredito
            ? "f.TipoFacturaRelacionada, f.TerminalFacturaRelacionada, f.NroFacturaRelacionada"
            : "NULL AS TipoFacturaRelacionada, NULL AS TerminalFacturaRelacionada, NULL AS NroFacturaRelacionada";

        using var cn = await Db.AbrirAsync(cs);
        var c = await cn.QueryFirstOrDefaultAsync<ComprobanteImpresion>(
            $@"SELECT f.Id, f.Fecha, f.Tipo, f.Terminal, f.Numero, f.TipoComprobanteAFIP, f.MontoTotal, f.MontoIva,
                     f.MontoNoGravado, f.ClienteNombre, f.ClienteCUIT, f.ClienteDomicilio, f.CAE, f.CAEVencimiento,
                     f.QRURLAFIP, fp.Nombre AS FormaPago, u.NombreUsuario AS Usuario, f.Anulada,
                     f.NumeroInterno, i.Nombre AS ClienteCondicionIva, ISNULL(i.EsConsumidorFinal, 0) AS ClienteEsConsumidorFinal,
                     f.Observaciones, f.MontoIvaTipoAfip4, f.MontoIvaTipoAfip5, f.MontoIvaTipoAfip6, f.ClienteLocalidad,
                     ISNULL(f.Impresa, 0) AS Impresa, {asociado}
              FROM {tabla} f
              LEFT JOIN dbo.FormaPago fp ON fp.Id = f.FormaPagoId
              LEFT JOIN dbo.Usuario u ON u.Id = f.UsuarioId
              LEFT JOIN dbo.Iva i ON i.Id = f.IvaId
              WHERE f.Id = @id", new { id });
        if (c == null) return null;
        c.EsNotaCredito = esNotaCredito;
        c.Renglones = (await cn.QueryAsync<RenglonImpresion>(
            $@"SELECT p.Nombre AS Producto, p.Codigo, d.CantidadRenglon, d.PU, d.MontoRenglon, d.IvaRenglon,
                     ISNULL(d.MontoIvaRenglon, 0) AS MontoIvaRenglon, ISNULL(d.DescuentoRenglon, 0) AS DescuentoRenglon,
                     {(esNotaCredito ? "CAST(0 AS DECIMAL(18,2))" : "ISNULL(d.MontoDescuentoRenglon, 0)")} AS MontoDescuentoRenglon
              FROM {detalle} d JOIN dbo.Producto p ON p.Id = d.ProductoId
              WHERE d.{fk} = @id ORDER BY d.Renglon", new { id })).ToList();
        // {mesletras}, {añonumero}, etc. en la descripción: con la fecha del comprobante (igual al reimprimir).
        foreach (var r in c.Renglones) r.Producto = DescripcionConFechas.Reemplazar(r.Producto, c.Fecha);
        return c;
    }

    /// <summary>
    /// Listado de comprobantes. Los filtros son opcionales: cliente y número buscan por
    /// parte del texto, y el estado distingue factura con CAE, pendiente, nota de pedido y anuladas.
    /// </summary>
    public async Task<List<ComprobanteListado>> ListarAsync(string cs, DateTime desde, DateTime hasta, Guid? usuarioId,
        string cliente = null, string numero = null, Guid? formaPagoId = null, string estado = null)
    {
        cliente = string.IsNullOrWhiteSpace(cliente) ? null : cliente.Trim();
        numero = string.IsNullOrWhiteSpace(numero) ? null : numero.Trim().TrimStart('0');
        if (numero == "") numero = null;

        var filtroEstado = estado switch
        {
            "CAE"     => "AND f.CAE IS NOT NULL AND f.CAE <> '' AND ISNULL(f.Anulada, 0) = 0 AND nc.NcCAE IS NULL",
            "SINCAE"  => "AND (f.CAE IS NULL OR f.CAE = '') AND ISNULL(f.Impresa, 0) = 0",
            "PEDIDO"  => "AND (f.CAE IS NULL OR f.CAE = '') AND ISNULL(f.Impresa, 0) = 1",
            "ANULADA" => "AND (ISNULL(f.Anulada, 0) = 1 OR (nc.NcCAE IS NOT NULL AND nc.NcCAE <> ''))",
            _ => ""
        };

        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<ComprobanteListado>(
            $@"SELECT TOP 500 f.Id, f.Fecha, f.Tipo, f.Terminal, f.Numero, f.ClienteNombre, f.MontoTotal, f.CAE,
                     fp.Nombre AS FormaPago, nc.NcId, nc.NcTipo, nc.NcTerminal, nc.NcNumero, nc.NcCAE,
                     f.NumeroInterno, ISNULL(f.Impresa, 0) AS Impresa, ISNULL(f.Anulada, 0) AS MarcadaAnulada
              FROM dbo.Factura f LEFT JOIN dbo.FormaPago fp ON fp.Id = f.FormaPagoId
              OUTER APPLY (SELECT TOP 1 n.Id AS NcId, n.Tipo AS NcTipo, n.Terminal AS NcTerminal, n.Numero AS NcNumero, n.CAE AS NcCAE
                           FROM dbo.NotaCredito n
                           WHERE n.TipoFacturaRelacionada = f.Tipo AND n.TerminalFacturaRelacionada = f.Terminal
                             AND n.NroFacturaRelacionada = f.Numero AND ISNULL(n.Anulada, 0) = 0
                           ORDER BY n.Fecha DESC) nc
              WHERE f.Fecha >= @desde AND f.Fecha < @hasta
                AND (@usuarioId IS NULL OR f.UsuarioId = @usuarioId)
                AND (@cliente IS NULL OR f.ClienteNombre LIKE '%' + @cliente + '%')
                AND (@formaPagoId IS NULL OR f.FormaPagoId = @formaPagoId)
                AND (@numero IS NULL
                     OR CAST(ISNULL(f.Numero, 0) AS varchar(20)) LIKE '%' + @numero + '%'
                     OR CAST(ISNULL(f.NumeroInterno, 0) AS varchar(20)) LIKE '%' + @numero + '%')
                {filtroEstado}
              ORDER BY f.Fecha DESC",
            new { desde = desde.Date, hasta = hasta.Date.AddDays(1), usuarioId, cliente, numero, formaPagoId })).ToList();
    }

    public static string QrDataUri(string url) =>
        string.IsNullOrEmpty(url) ? null : "data:image/png;base64," + Convert.ToBase64String(QrPng(url));

    // ------------------------------------------------------------------ auxiliares

    private static byte[] QrPng(string url)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
        return new PngByteQRCode(data).GetGraphic(6);
    }

    /// <summary>Código de tipo de documento de ARCA según el nombre en dbo.TipoDocumento (null si no se sabe).</summary>
    public static int? CodigoTipoDocArca(string nombre)
    {
        var n = (nombre ?? "").ToUpperInvariant();
        if (n.Contains("CUIT")) return 80;
        if (n.Contains("CUIL")) return 86;
        if (n.Contains("CDI")) return 87;
        if (n.Contains("DNI")) return 96;
        if (n.Contains("PASAPORTE")) return 94;
        return null;
    }

    private class FacturaCab
    {
        public int? ClienteTipoDocAFIP { get; set; }
        public bool Impresa { get; set; }
        public Guid Id { get; set; }
        public DateTime Fecha { get; set; }
        public string Tipo { get; set; }
        public decimal MontoTotal { get; set; }
        public Guid IvaId { get; set; }
        public string ClienteCUIT { get; set; }
        public string CAE { get; set; }
        public int Terminal { get; set; }
        public int? Numero { get; set; }
    }

    private class FacturaCompleta
    {
        public Guid Id { get; set; }
        public DateTime Fecha { get; set; }
        public string Tipo { get; set; }
        public int Terminal { get; set; }
        public int? Numero { get; set; }
        public int? TipoComprobanteAFIP { get; set; }
        public int? CantidadRenglones { get; set; }
        public decimal MontoTotal { get; set; }
        public decimal MontoIva { get; set; }
        public decimal? Iva { get; set; }
        public decimal? MontoNoGravado { get; set; }
        public decimal? MontoExento { get; set; }
        public Guid IvaId { get; set; }
        public Guid FormaPagoId { get; set; }
        public Guid? ClienteId { get; set; }
        public string ClienteNombre { get; set; }
        public string ClienteCUIT { get; set; }
        public string ClienteDomicilio { get; set; }
        public string ClienteLocalidad { get; set; }
        public string CAE { get; set; }
    }

    private class NotaCreditoCab
    {
        public Guid Id { get; set; }
        public string Tipo { get; set; }
        public int Terminal { get; set; }
        public int? Numero { get; set; }
        public decimal MontoTotal { get; set; }
        public Guid IvaId { get; set; }
        public string ClienteCUIT { get; set; }
        public string CAE { get; set; }
        public int? NroFacturaRelacionada { get; set; }
        public string TipoFacturaRelacionada { get; set; }
        public int? TerminalFacturaRelacionada { get; set; }
    }

    private class DetalleCalc
    {
        public decimal MontoRenglon { get; set; }
        public decimal MontoIvaRenglon { get; set; }
        public decimal IvaRenglon { get; set; }
        public decimal MontoNoGravadoRenglon { get; set; }
    }

    private class IvaFila
    {
        public Guid Id { get; set; }
        public decimal Porcentaje { get; set; }
        public string LetraComprobante { get; set; }
        public bool EsResponsableInscripto { get; set; }
        public bool EsNoResponsable { get; set; }
        public bool EsExento { get; set; }
        public bool EsMonotributo { get; set; }
        public bool EsConsumidorFinal { get; set; }

        public CondicionReceptor Condicion =>
            EsResponsableInscripto ? CondicionReceptor.ResponsableInscripto :
            EsMonotributo ? CondicionReceptor.Monotributo :
            EsExento ? CondicionReceptor.Exento :
            EsNoResponsable ? CondicionReceptor.NoResponsable : CondicionReceptor.ConsumidorFinal;
    }

    private static async Task<IvaFila> IvaCondicionAsync(IDbConnection cn, CondicionReceptor? cond, Guid? id)
    {
        const string cols = @"Id, Porcentaje, LetraComprobante,
            ISNULL(EsResponsableInscripto,0) AS EsResponsableInscripto, ISNULL(EsNoResponsable,0) AS EsNoResponsable,
            ISNULL(EsExento,0) AS EsExento, ISNULL(EsMonotributo,0) AS EsMonotributo,
            ISNULL(EsConsumidorFinal,0) AS EsConsumidorFinal";
        if (id.HasValue)
            return await cn.QueryFirstOrDefaultAsync<IvaFila>($"SELECT {cols} FROM dbo.Iva WHERE Id = @id", new { id });

        string flag = cond switch
        {
            CondicionReceptor.ResponsableInscripto => "EsResponsableInscripto",
            CondicionReceptor.Monotributo => "EsMonotributo",
            CondicionReceptor.Exento => "EsExento",
            CondicionReceptor.NoResponsable => "EsNoResponsable",
            _ => "EsConsumidorFinal"
        };
        return await cn.QueryFirstOrDefaultAsync<IvaFila>(
            $"SELECT TOP 1 {cols} FROM dbo.Iva WHERE {flag} = 1 ORDER BY Id");
    }

    private static int CondicionArca(CondicionReceptor c) => c switch
    {
        CondicionReceptor.ResponsableInscripto => WsfeCondicionIvaReceptor.IVAResponsableInscripto,
        CondicionReceptor.Monotributo => WsfeCondicionIvaReceptor.ResponsableMonotributo,
        CondicionReceptor.Exento => WsfeCondicionIvaReceptor.IVASujetoExento,
        CondicionReceptor.NoResponsable => WsfeCondicionIvaReceptor.SujetoNoCategorizado,
        _ => WsfeCondicionIvaReceptor.ConsumidorFinal
    };

    /// <summary>
    /// Código de alícuota de ARCA para un porcentaje: primero según dbo.IvaCondicionAFIP de la base
    /// del cliente (columna TipoAFIPWS, como el SP GetSummaryTipoIvaAFIPWS de APC); si no está, el estándar.
    /// </summary>
    private static int CodigoAlicuota(decimal porc, Dictionary<decimal, int> tabla)
    {
        if (tabla.TryGetValue(porc, out var codigo)) return codigo;
        return porc switch
        {
            10.5m => 4,
            21m => 5,
            27m => 6,
            5m => 8,
            2.5m => 9,
            _ => 5
        };
    }

    private static async Task<Dictionary<decimal, int>> CodigosAlicuotaAsync(IDbConnection cn)
    {
        var d = new Dictionary<decimal, int>();
        try
        {
            var filas = await cn.QueryAsync<(string Porcentaje, int? TipoAFIPWS)>(
                "SELECT Porcentaje, TipoAFIPWS FROM dbo.IvaCondicionAFIP WHERE ISNULL(Activo, 1) = 1");
            foreach (var f in filas)
            {
                // Solo alícuotas con IVA (código 4 o mayor); 0 %, exento y no gravado van como no gravado
                if (f.TipoAFIPWS is not >= 4) continue;
                var texto = (f.Porcentaje ?? "").Replace("%", "").Replace(",", ".").Trim();
                if (decimal.TryParse(texto, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var porc) && porc > 0)
                    d[porc] = f.TipoAFIPWS.Value;
            }
        }
        catch (Exception)
        {
            // Base sin la tabla o sin la columna: se usan los códigos estándar de ARCA
        }
        return d;
    }

    public static string NormalizarDocumento(string doc) =>
        new string((doc ?? "").Where(char.IsDigit).ToArray());

    private static string Recortar(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length > max ? s.Substring(0, max) : s);

    private static ResultadoEmision Falla(string msg) => new ResultadoEmision { Ok = false, Mensaje = msg };
}

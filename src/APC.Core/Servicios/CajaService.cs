using APC.Core.Datos;
using APC.Core.Modelos;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Turnos de caja (apertura/cierre, tabla nueva dbo.CajaTurno) + arqueo.
/// Los movimientos se calculan desde Factura, NotaCredito, CajaIngreso y EgresoCaja (ver ArqueoAsync).
/// </summary>
public class CajaService
{
    /// <summary>
    /// Turno abierto de quien factura. Si el usuario está en una caja de la sucursal (puestoCajaId) y ya se
    /// corrió el script 28, el turno es el de esa caja, lo haya abierto quien sea. Si no, el del usuario
    /// (como antes). Un turno por usuario que quedó abierto de antes se sigue devolviendo para poder cerrarlo.
    /// </summary>
    public async Task<CajaTurno> TurnoAbiertoAsync(string cs, Guid usuarioId, Guid? puestoCajaId = null)
    {
        using var cn = await Db.AbrirAsync(cs);
        var porCaja = await ExisteColumnaPuestoAsync(cn);
        if (porCaja && puestoCajaId != null)
        {
            var deCaja = await TurnoAbiertoDeCajaAsync(cn, puestoCajaId.Value);
            if (deCaja != null) return deCaja;
        }
        return await cn.QueryFirstOrDefaultAsync<CajaTurno>(
            $@"SELECT TOP 1 * FROM dbo.CajaTurno WHERE UsuarioId = @u AND FechaCierre IS NULL
               {(porCaja ? "AND PuestoCajaId IS NULL" : "")} ORDER BY FechaApertura DESC",
            new { u = usuarioId });
    }

    private static async Task<CajaTurno> TurnoAbiertoDeCajaAsync(System.Data.IDbConnection cn, Guid puestoCajaId)
    {
        var t = await cn.QueryFirstOrDefaultAsync<CajaTurno>(
            @"SELECT TOP 1 t.*, u.NombreUsuario AS Usuario,
                     ISNULL(NULLIF(pc.Nombre, ''), 'Caja ' + CAST(pc.Numero AS VARCHAR(10))) AS Puesto
              FROM dbo.CajaTurno t
              LEFT JOIN dbo.Usuario u ON u.Id = t.UsuarioId
              LEFT JOIN dbo.PuestoCaja pc ON pc.Id = t.PuestoCajaId
              WHERE t.PuestoCajaId = @p AND t.FechaCierre IS NULL ORDER BY t.FechaApertura DESC",
            new { p = puestoCajaId });
        if (t != null) t.EsDeCaja = true;
        return t;
    }

    /// <summary>
    /// Abre la caja. Con puestoCajaId (y el script 28) se abre esa caja de la sucursal: el turno queda a
    /// nombre de quien la abrió pero es de la caja. Si otro la abrió un instante antes, devuelve ese turno.
    /// </summary>
    public async Task<CajaTurno> AbrirAsync(string cs, Guid usuarioId, decimal montoInicial, Guid? sucursalId = null, Guid? puestoCajaId = null)
    {
        var abierto = await TurnoAbiertoAsync(cs, usuarioId, puestoCajaId);
        if (abierto != null) return abierto;
        using var cn = await Db.AbrirAsync(cs);
        var porCaja = puestoCajaId != null && await ExisteColumnaPuestoAsync(cn);
        var t = new CajaTurno
        {
            Id = Guid.NewGuid(), UsuarioId = usuarioId, FechaApertura = DateTime.Now,
            MontoInicial = montoInicial, SucursalId = sucursalId,
            PuestoCajaId = porCaja ? puestoCajaId : null, EsDeCaja = porCaja
        };
        if (porCaja)
        {
            try
            {
                await cn.ExecuteAsync(
                    @"INSERT INTO dbo.CajaTurno (Id, UsuarioId, FechaApertura, MontoInicial, SucursalId, PuestoCajaId)
                      VALUES (@Id, @UsuarioId, @FechaApertura, @MontoInicial, @SucursalId, @PuestoCajaId)", t);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // Otro cajero abrió la misma caja al mismo tiempo: se usa la suya.
                return await TurnoAbiertoDeCajaAsync(cn, puestoCajaId.Value);
            }
            return await TurnoAbiertoDeCajaAsync(cn, puestoCajaId.Value) ?? t;
        }
        try
        {
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.CajaTurno (Id, UsuarioId, FechaApertura, MontoInicial, SucursalId)
                  VALUES (@Id, @UsuarioId, @FechaApertura, @MontoInicial, @SucursalId)", t);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            // Todavía no se corrió el script 14 (caja por sucursal)
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.CajaTurno (Id, UsuarioId, FechaApertura, MontoInicial)
                  VALUES (@Id, @UsuarioId, @FechaApertura, @MontoInicial)", t);
        }
        return t;
    }

    /// <summary>Ya se corrió el script 28 (apertura por caja).</summary>
    public async Task<bool> AperturaPorCajaAsync(string cs)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await ExisteColumnaPuestoAsync(cn);
    }

    private static async Task<bool> ExisteColumnaPuestoAsync(System.Data.IDbConnection cn) =>
        await cn.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN COL_LENGTH('dbo.CajaTurno', 'PuestoCajaId') IS NULL OR OBJECT_ID('dbo.PuestoCajaSesion', 'U') IS NULL THEN 0 ELSE 1 END") == 1;

    /// <summary>
    /// Condición SQL "el movimiento (alias x) lo hizo alguien mientras estaba en la caja @p": así se reparte
    /// lo vendido entre cajas sin tocar las tablas del sistema de escritorio (un usuario está en una sola caja a la vez).
    /// </summary>
    /// <summary>Condición SQL "el usuario del movimiento (alias x) estaba en alguna caja en ese momento".</summary>
    internal static string EnAlgunaCaja(string x) =>
        $@"EXISTS (SELECT 1 FROM dbo.PuestoCajaSesion pcs WHERE pcs.UsuarioId = {x}.UsuarioId
                   AND {x}.Fecha >= pcs.FechaIngreso AND (pcs.FechaSalida IS NULL OR {x}.Fecha < pcs.FechaSalida))";

    private static string EnLaCaja(string x) =>
        $@"EXISTS (SELECT 1 FROM dbo.PuestoCajaSesion pcs WHERE pcs.CajaId = @p AND pcs.UsuarioId = {x}.UsuarioId
                   AND {x}.Fecha >= pcs.FechaIngreso AND (pcs.FechaSalida IS NULL OR {x}.Fecha < pcs.FechaSalida))";

    /// <summary>
    /// Cajas de un día para la supervisión. Con sucursalId se limita a esa sucursal
    /// (lo que ve un supervisor); sin sucursalId se ven todas (el administrador).
    /// </summary>
    public async Task<List<CajaTurno>> TurnosDelDiaAsync(string cs, DateTime dia, Guid? sucursalId, bool soloAbiertos = false)
    {
        using var cn = await Db.AbrirAsync(cs);
        var columnaSucursal = "NULL AS SucursalId, '' AS Sucursal";
        var filtroSucursal = "";
        if (await ExisteColumnaSucursalAsync(cn))
        {
            columnaSucursal = "t.SucursalId, s.Nombre AS Sucursal";
            if (sucursalId != null) filtroSucursal = "AND t.SucursalId = @sucursalId";
        }

        // Con el script 28, el turno de una caja suma lo vendido por cualquiera mientras estuvo en ella.
        var porCaja = await ExisteColumnaPuestoAsync(cn);
        var ventasDeCaja = EnLaCaja("f").Replace("@p", "t.PuestoCajaId");
        var ventas = porCaja
            ? $@"CASE WHEN t.PuestoCajaId IS NULL THEN
                    (SELECT ISNULL(SUM(f.MontoTotal), 0) FROM dbo.Factura f
                     WHERE f.UsuarioId = t.UsuarioId AND f.Fecha >= t.FechaApertura AND f.Fecha < ISNULL(t.FechaCierre, GETDATE()) AND ISNULL(f.Anulada, 0) = 0)
                 ELSE
                    (SELECT ISNULL(SUM(f.MontoTotal), 0) FROM dbo.Factura f
                     WHERE f.Fecha >= t.FechaApertura AND f.Fecha < ISNULL(t.FechaCierre, GETDATE()) AND ISNULL(f.Anulada, 0) = 0 AND {ventasDeCaja})
                 END"
            : @"(SELECT ISNULL(SUM(f.MontoTotal), 0) FROM dbo.Factura f
                 WHERE f.UsuarioId = t.UsuarioId AND f.Fecha >= t.FechaApertura
                   AND f.Fecha < ISNULL(t.FechaCierre, GETDATE()) AND ISNULL(f.Anulada, 0) = 0)";
        var columnasCaja = porCaja
            ? @", t.PuestoCajaId, t.UsuarioCierreId, uc.NombreUsuario AS UsuarioCierre,
                 CAST(CASE WHEN t.PuestoCajaId IS NULL THEN 0 ELSE 1 END AS BIT) AS EsDeCaja,
                 ISNULL(NULLIF(pc.Nombre, ''), 'Caja ' + CAST(pc.Numero AS VARCHAR(10))) AS Puesto"
            : "";
        var joinsCaja = porCaja
            ? @"LEFT JOIN dbo.PuestoCaja pc ON pc.Id = t.PuestoCajaId
               LEFT JOIN dbo.Usuario uc ON uc.Id = t.UsuarioCierreId"
            : "";

        return (await cn.QueryAsync<CajaTurno>(
            $@"SELECT t.Id, t.UsuarioId, t.FechaApertura, t.MontoInicial, t.FechaCierre,
                      t.EfectivoEsperado, t.EfectivoContado, t.Diferencia, t.Observaciones,
                      {columnaSucursal}, u.NombreUsuario AS Usuario,
                      {ventas} AS Ventas {columnasCaja}
               FROM dbo.CajaTurno t
               LEFT JOIN dbo.Usuario u ON u.Id = t.UsuarioId
               {(columnaSucursal.StartsWith("NULL") ? "" : "LEFT JOIN dbo.Sucursal s ON s.Id = t.SucursalId")}
               {joinsCaja}
               WHERE {(soloAbiertos ? "t.FechaCierre IS NULL" : "t.FechaApertura >= @desde AND t.FechaApertura < @hasta")} {filtroSucursal}
               ORDER BY t.FechaApertura DESC",
            new { desde = dia.Date, hasta = dia.Date.AddDays(1), sucursalId })).ToList();
    }

    /// <summary>
    /// Para sucursales que no exigen abrir caja: lo vendido en el día por cada usuario, armado como una
    /// "caja" de todo el día (Id vacío, de 00:00 a 24:00, sin monto inicial) para poder ver su arqueo
    /// con <see cref="ArqueoAsync"/>. Cuenta todo lo no anulado: con CAE, sin CAE y notas de pedido.
    /// </summary>
    public async Task<List<CajaTurno>> MovimientosSinCajaAsync(string cs, DateTime dia, int? puntoVenta, Guid? usuarioId = null)
    {
        using var cn = await Db.AbrirAsync(cs);
        var filas = (await cn.QueryAsync<CajaTurno>(
            $@"SELECT f.UsuarioId, u.NombreUsuario AS Usuario, SUM(f.MontoTotal) AS Ventas
               FROM dbo.Factura f
               LEFT JOIN dbo.Usuario u ON u.Id = f.UsuarioId
               WHERE f.Fecha >= @desde AND f.Fecha < @hasta AND ISNULL(f.Anulada, 0) = 0
                 {(puntoVenta != null ? "AND f.Terminal = @puntoVenta" : "")}
                 {(usuarioId != null ? "AND f.UsuarioId = @usuarioId" : "")}
               GROUP BY f.UsuarioId, u.NombreUsuario
               ORDER BY u.NombreUsuario",
            new { desde = dia.Date, hasta = dia.Date.AddDays(1), puntoVenta, usuarioId })).ToList();

        foreach (var c in filas)
        {
            c.Id = Guid.Empty;
            c.FechaApertura = dia.Date;
            c.FechaCierre = dia.Date.AddDays(1);
            c.MontoInicial = 0;
        }
        return filas;
    }

    private static async Task<bool> ExisteColumnaSucursalAsync(System.Data.IDbConnection cn) =>
        await cn.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN COL_LENGTH('dbo.CajaTurno', 'SucursalId') IS NULL THEN 0 ELSE 1 END") == 1;

    private class MovimientoCaja
    {
        public string Tipo { get; set; }
        public string FormaPago { get; set; }
        public decimal Monto { get; set; }
    }

    private static readonly (string Codigo, string Titulo)[] TiposArqueo =
    {
        ("F", "Facturas"),
        ("S", "Facturas sin CAE (pendientes)"),
        ("P", "Notas de pedido"),
        ("N", "Notas de crédito"),
        ("C", "Ingresos y retiros de caja")
    };

    /// <summary>
    /// Arqueo del turno calculado directo de los comprobantes del usuario entre la apertura y el cierre:
    /// facturas con CAE, facturas pendientes de CAE, notas de pedido (no fiscales, Impresa sin CAE),
    /// notas de crédito (restan) e ingresos/retiros de efectivo. No depende de la vista VArqueoCaja.
    /// </summary>
    public async Task<ArqueoResumen> ArqueoAsync(string cs, CajaTurno turno)
    {
        var hasta = turno.FechaCierre ?? DateTime.Now.AddMinutes(1);
        using var cn = await Db.AbrirAsync(cs);
        // Turno de una caja (script 28): lo que hizo cualquier cajero mientras estuvo en esa caja.
        // Turno por usuario: lo que hizo ese usuario.
        var deCaja = turno.PuestoCajaId != null && turno.EsDeCaja;
        string Quien(string x) => deCaja ? EnLaCaja(x)
            : turno.FueraDeCaja ? $"{x}.UsuarioId = @u AND NOT {EnAlgunaCaja(x)}"
            : $"{x}.UsuarioId = @u";
        var movs = (await cn.QueryAsync<MovimientoCaja>(
            $@"SELECT CASE WHEN f.CAE IS NOT NULL AND f.CAE <> '' THEN 'F'
                          WHEN ISNULL(f.Impresa, 0) = 1 THEN 'P' ELSE 'S' END AS Tipo,
                     ISNULL(fp.Nombre, '(sin forma de pago)') AS FormaPago, f.MontoTotal AS Monto
              FROM dbo.Factura f LEFT JOIN dbo.FormaPago fp ON fp.Id = f.FormaPagoId
              WHERE {Quien("f")} AND f.Fecha >= @desde AND f.Fecha < @hasta AND ISNULL(f.Anulada, 0) = 0
              UNION ALL
              SELECT 'N', ISNULL(fp.Nombre, '(sin forma de pago)'), -n.MontoTotal
              FROM dbo.NotaCredito n LEFT JOIN dbo.FormaPago fp ON fp.Id = n.FormaPagoId
              WHERE {Quien("n")} AND n.Fecha >= @desde AND n.Fecha < @hasta
                AND n.CAE IS NOT NULL AND n.CAE <> '' AND ISNULL(n.Anulada, 0) = 0
              UNION ALL
              SELECT 'C', 'Ingreso de efectivo', ci.Monto FROM dbo.CajaIngreso ci
              WHERE {Quien("ci")} AND ci.Fecha >= @desde AND ci.Fecha < @hasta
              UNION ALL
              SELECT 'C', 'Retiro de efectivo', -ec.Monto FROM dbo.EgresoCaja ec
              WHERE {Quien("ec")} AND ec.Fecha >= @desde AND ec.Fecha < @hasta",
            new { u = turno.UsuarioId, p = turno.PuestoCajaId, desde = turno.FechaApertura, hasta })).ToList();

        var r = new ArqueoResumen { Turno = turno };
        foreach (var (codigo, titulo) in TiposArqueo)
        {
            var delTipo = movs.Where(m => m.Tipo == codigo).ToList();
            if (delTipo.Count == 0) continue;
            r.Grupos.Add(new ArqueoGrupo
            {
                Codigo = codigo,
                Titulo = titulo,
                Formas = delTipo.GroupBy(m => m.FormaPago.Trim())
                    .Select(g => new ArqueoFormaPago { FormaPago = g.Key, Comprobantes = g.Count(), Total = g.Sum(x => x.Monto) })
                    .OrderBy(x => x.FormaPago).ToList()
            });
        }

        var fiscales = movs.Where(m => m.Tipo is "F" or "S").ToList();
        r.VentasFiscales = fiscales.Sum(m => m.Monto);
        r.CantidadFiscales = fiscales.Count;
        var pedidos = movs.Where(m => m.Tipo == "P").ToList();
        r.VentasNoFiscales = pedidos.Sum(m => m.Monto);
        r.CantidadNoFiscales = pedidos.Count;
        r.NotasCredito = movs.Where(m => m.Tipo == "N").Sum(m => m.Monto);
        r.IngresosEgresos = movs.Where(m => m.Tipo == "C").Sum(m => m.Monto);

        // Efectivo esperado: monto inicial + todo lo cobrado o devuelto en efectivo (fiscal y no fiscal) + ingresos − retiros
        r.EfectivoEsperado = turno.MontoInicial
            + movs.Where(m => m.Tipo == "C" || FormaPagoItem.EsNombreEfectivo(m.FormaPago)).Sum(m => m.Monto);
        return r;
    }

    public async Task RegistrarIngresoAsync(string cs, Guid usuarioId, decimal monto, string concepto)
    {
        using var cn = await Db.AbrirAsync(cs);
        await cn.ExecuteAsync(
            "INSERT INTO dbo.CajaIngreso (Id, UsuarioId, Fecha, Monto, Concepto) VALUES (NEWID(), @u, GETDATE(), @m, @c)",
            new { u = usuarioId, m = monto, c = concepto });
    }

    public async Task RegistrarEgresoAsync(string cs, Guid usuarioId, decimal monto, string concepto)
    {
        using var cn = await Db.AbrirAsync(cs);
        await cn.ExecuteAsync(
            "INSERT INTO dbo.EgresoCaja (Id, UsuarioId, Fecha, Monto, Concepto) VALUES (NEWID(), @u, GETDATE(), @m, @c)",
            new { u = usuarioId, m = monto, c = concepto });
    }

    /// <summary>
    /// Cierra el turno. En una caja compartida puede pasar que otro cajero la cierre un instante antes:
    /// en ese caso no se pisa su cierre y se devuelve el que quedó guardado.
    /// </summary>
    public async Task<ArqueoResumen> CerrarAsync(string cs, CajaTurno turno, decimal efectivoContado, string observaciones, Guid? usuarioCierreId = null)
    {
        var arqueo = await ArqueoAsync(cs, turno);
        using var cn = await Db.AbrirAsync(cs);
        var conCierre = await cn.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN COL_LENGTH('dbo.CajaTurno', 'UsuarioCierreId') IS NULL THEN 0 ELSE 1 END") == 1;
        turno.FechaCierre = DateTime.Now;
        turno.EfectivoEsperado = arqueo.EfectivoEsperado;
        turno.EfectivoContado = efectivoContado;
        turno.Diferencia = efectivoContado - arqueo.EfectivoEsperado;
        turno.Observaciones = observaciones;
        turno.UsuarioCierreId = usuarioCierreId;
        var filas = await cn.ExecuteAsync(
            $@"UPDATE dbo.CajaTurno SET FechaCierre = @FechaCierre, EfectivoEsperado = @EfectivoEsperado,
                     EfectivoContado = @EfectivoContado, Diferencia = @Diferencia, Observaciones = @Observaciones
                     {(conCierre ? ", UsuarioCierreId = @UsuarioCierreId" : "")}
              WHERE Id = @Id AND FechaCierre IS NULL", turno);
        if (filas == 0)
        {
            var guardado = await ObtenerTurnoAsync(cs, turno.Id);
            if (guardado != null) arqueo = await ArqueoAsync(cs, guardado);
            return arqueo;
        }
        arqueo.Turno = turno;
        return arqueo;
    }

    public async Task<CajaTurno> ObtenerTurnoAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        if (!await ExisteColumnaPuestoAsync(cn))
            return await cn.QueryFirstOrDefaultAsync<CajaTurno>("SELECT * FROM dbo.CajaTurno WHERE Id = @id", new { id });
        var t = await cn.QueryFirstOrDefaultAsync<CajaTurno>(
            @"SELECT t.*, u.NombreUsuario AS Usuario, uc.NombreUsuario AS UsuarioCierre,
                     ISNULL(NULLIF(pc.Nombre, ''), 'Caja ' + CAST(pc.Numero AS VARCHAR(10))) AS Puesto
              FROM dbo.CajaTurno t
              LEFT JOIN dbo.Usuario u ON u.Id = t.UsuarioId
              LEFT JOIN dbo.Usuario uc ON uc.Id = t.UsuarioCierreId
              LEFT JOIN dbo.PuestoCaja pc ON pc.Id = t.PuestoCajaId
              WHERE t.Id = @id", new { id });
        if (t != null) t.EsDeCaja = t.PuestoCajaId != null;
        return t;
    }

    /// <summary>Junta varios arqueos en uno: mismas formas de pago se suman.</summary>
    public static ArqueoResumen Sumar(IEnumerable<ArqueoResumen> partes)
    {
        var lista = partes.ToList();
        const string orden = "FSPNC";
        return new ArqueoResumen
        {
            VentasFiscales = lista.Sum(a => a.VentasFiscales),
            CantidadFiscales = lista.Sum(a => a.CantidadFiscales),
            VentasNoFiscales = lista.Sum(a => a.VentasNoFiscales),
            CantidadNoFiscales = lista.Sum(a => a.CantidadNoFiscales),
            NotasCredito = lista.Sum(a => a.NotasCredito),
            IngresosEgresos = lista.Sum(a => a.IngresosEgresos),
            EfectivoEsperado = lista.Sum(a => a.EfectivoEsperado),
            Grupos = lista.SelectMany(a => a.Grupos)
                .GroupBy(g => g.Codigo)
                .OrderBy(g => { var i = orden.IndexOf(g.Key ?? "?", StringComparison.Ordinal); return i < 0 ? 99 : i; })
                .Select(g => new ArqueoGrupo
                {
                    Codigo = g.Key,
                    Titulo = g.First().Titulo,
                    Formas = g.SelectMany(x => x.Formas)
                        .GroupBy(x => x.FormaPago)
                        .Select(x => new ArqueoFormaPago { FormaPago = x.Key, Comprobantes = x.Sum(y => y.Comprobantes), Total = x.Sum(y => y.Total) })
                        .OrderBy(x => x.FormaPago).ToList()
                }).ToList()
        };
    }
}

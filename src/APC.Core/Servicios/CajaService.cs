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
    public async Task<CajaTurno> TurnoAbiertoAsync(string cs, Guid usuarioId)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.QueryFirstOrDefaultAsync<CajaTurno>(
            "SELECT TOP 1 * FROM dbo.CajaTurno WHERE UsuarioId = @u AND FechaCierre IS NULL ORDER BY FechaApertura DESC",
            new { u = usuarioId });
    }

    public async Task<CajaTurno> AbrirAsync(string cs, Guid usuarioId, decimal montoInicial)
    {
        var abierto = await TurnoAbiertoAsync(cs, usuarioId);
        if (abierto != null) return abierto;
        using var cn = await Db.AbrirAsync(cs);
        var t = new CajaTurno { Id = Guid.NewGuid(), UsuarioId = usuarioId, FechaApertura = DateTime.Now, MontoInicial = montoInicial };
        await cn.ExecuteAsync(
            "INSERT INTO dbo.CajaTurno (Id, UsuarioId, FechaApertura, MontoInicial) VALUES (@Id, @UsuarioId, @FechaApertura, @MontoInicial)", t);
        return t;
    }

    private class MovimientoCaja
    {
        public string Tipo { get; set; }
        public string FormaPago { get; set; }
        public decimal Monto { get; set; }
    }

    private static readonly (string Codigo, string Titulo)[] TiposArqueo =
    {
        ("F", "Facturas (fiscal)"),
        ("S", "Facturas sin CAE (pendientes)"),
        ("P", "Notas de pedido (no fiscal)"),
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
        var movs = (await cn.QueryAsync<MovimientoCaja>(
            @"SELECT CASE WHEN f.CAE IS NOT NULL AND f.CAE <> '' THEN 'F'
                          WHEN ISNULL(f.Impresa, 0) = 1 THEN 'P' ELSE 'S' END AS Tipo,
                     ISNULL(fp.Nombre, '(sin forma de pago)') AS FormaPago, f.MontoTotal AS Monto
              FROM dbo.Factura f LEFT JOIN dbo.FormaPago fp ON fp.Id = f.FormaPagoId
              WHERE f.UsuarioId = @u AND f.Fecha >= @desde AND f.Fecha < @hasta AND ISNULL(f.Anulada, 0) = 0
              UNION ALL
              SELECT 'N', ISNULL(fp.Nombre, '(sin forma de pago)'), -n.MontoTotal
              FROM dbo.NotaCredito n LEFT JOIN dbo.FormaPago fp ON fp.Id = n.FormaPagoId
              WHERE n.UsuarioId = @u AND n.Fecha >= @desde AND n.Fecha < @hasta
                AND n.CAE IS NOT NULL AND n.CAE <> '' AND ISNULL(n.Anulada, 0) = 0
              UNION ALL
              SELECT 'C', 'Ingreso de efectivo', Monto FROM dbo.CajaIngreso
              WHERE UsuarioId = @u AND Fecha >= @desde AND Fecha < @hasta
              UNION ALL
              SELECT 'C', 'Retiro de efectivo', -Monto FROM dbo.EgresoCaja
              WHERE UsuarioId = @u AND Fecha >= @desde AND Fecha < @hasta",
            new { u = turno.UsuarioId, desde = turno.FechaApertura, hasta })).ToList();

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

    public async Task<ArqueoResumen> CerrarAsync(string cs, CajaTurno turno, decimal efectivoContado, string observaciones)
    {
        var arqueo = await ArqueoAsync(cs, turno);
        using var cn = await Db.AbrirAsync(cs);
        turno.FechaCierre = DateTime.Now;
        turno.EfectivoEsperado = arqueo.EfectivoEsperado;
        turno.EfectivoContado = efectivoContado;
        turno.Diferencia = efectivoContado - arqueo.EfectivoEsperado;
        turno.Observaciones = observaciones;
        await cn.ExecuteAsync(
            @"UPDATE dbo.CajaTurno SET FechaCierre = @FechaCierre, EfectivoEsperado = @EfectivoEsperado,
                     EfectivoContado = @EfectivoContado, Diferencia = @Diferencia, Observaciones = @Observaciones
              WHERE Id = @Id", turno);
        arqueo.Turno = turno;
        return arqueo;
    }

    public async Task<CajaTurno> ObtenerTurnoAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.QueryFirstOrDefaultAsync<CajaTurno>("SELECT * FROM dbo.CajaTurno WHERE Id = @id", new { id });
    }
}

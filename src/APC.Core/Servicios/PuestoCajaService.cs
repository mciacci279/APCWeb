using APC.Core.Datos;
using APC.Core.Modelos;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Cajas de cada sucursal y quién trabaja en cada una (tablas dbo.PuestoCaja y dbo.PuestoCajaSesion, script 27).
/// Reglas:
/// - Una caja la usa un solo usuario a la vez (lo asegura además el índice UX_PuestoCajaSesion_Abierta).
/// - Un usuario está en una sola caja: al tomar otra, la anterior se cierra con motivo CAMBIO.
/// - La pantalla avisa cada minuto (latido). Sin aviso por <see cref="MinutosInactividad"/> minutos,
///   la caja se considera libre y la sesión se cierra con motivo INACTIVIDAD.
/// Todas las horas son las del servidor SQL (GETDATE()), así no dependen del reloj de cada PC.
/// </summary>
public class PuestoCajaService
{
    public const int MinutosInactividad = 10;

    // Sesión viva: abierta y con un latido reciente.
    private const string Viva = "s.FechaSalida IS NULL AND s.UltimoLatido >= DATEADD(MINUTE, -10, GETDATE())";

    /// <summary>Si ya se corrió el script 27 en la base de la empresa.</summary>
    public async Task<bool> ExisteAsync(string cs)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN OBJECT_ID(N'dbo.PuestoCaja', N'U') IS NULL OR OBJECT_ID(N'dbo.PuestoCajaSesion', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
    }

    // ------------------------------------------------------------------ cajas

    /// <summary>Cajas de una sucursal (o de todas) con quién la está usando ahora.</summary>
    public async Task<List<PuestoCaja>> ListarAsync(string cs, Guid? sucursalId, bool incluirInactivas = false)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<PuestoCaja>(
            $@"SELECT c.Id, c.SucursalId, c.Numero, c.Nombre, c.MercadoPagoCajaId, c.Activa, c.FechaAlta,
                      s.Id AS SesionId, s.UsuarioId, u.NombreUsuario AS Usuario, s.FechaIngreso AS Desde, s.UltimoLatido
               FROM dbo.PuestoCaja c
               LEFT JOIN dbo.PuestoCajaSesion s ON s.CajaId = c.Id AND {Viva}
               LEFT JOIN dbo.Usuario u ON u.Id = s.UsuarioId
               WHERE (@sucursalId IS NULL OR c.SucursalId = @sucursalId)
                 AND (@incluirInactivas = 1 OR c.Activa = 1)
               ORDER BY c.SucursalId, c.Numero",
            new { sucursalId, incluirInactivas })).ToList();
    }

    /// <summary>Cuántas cajas activas tiene la sucursal. Con más de una, el usuario elige al ingresar.</summary>
    public async Task<int> ContarActivasAsync(string cs, Guid sucursalId)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.PuestoCaja WHERE SucursalId = @sucursalId AND Activa = 1", new { sucursalId });
    }

    /// <summary>Crea (Id vacío, toma el número siguiente de la sucursal) o actualiza una caja.</summary>
    public async Task<string> GuardarAsync(string cs, PuestoCaja c)
    {
        c.Nombre = string.IsNullOrWhiteSpace(c.Nombre) ? null : c.Nombre.Trim();
        if (c.Nombre?.Length > 60) return "El nombre de la caja no puede superar los 60 caracteres.";
        if (c.SucursalId == Guid.Empty) return "Falta la sucursal.";

        using var cn = await Db.AbrirAsync(cs);
        if (c.Id == Guid.Empty)
        {
            c.Id = Guid.NewGuid();
            c.Numero = await cn.ExecuteScalarAsync<int>(
                "SELECT ISNULL(MAX(Numero), 0) + 1 FROM dbo.PuestoCaja WHERE SucursalId = @SucursalId", c);
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.PuestoCaja (Id, SucursalId, Numero, Nombre, MercadoPagoCajaId, Activa)
                  VALUES (@Id, @SucursalId, @Numero, @Nombre, @MercadoPagoCajaId, 1)", c);
        }
        else
        {
            await cn.ExecuteAsync(
                "UPDATE dbo.PuestoCaja SET Nombre = @Nombre, MercadoPagoCajaId = @MercadoPagoCajaId, Activa = @Activa WHERE Id = @Id", c);
            // Una caja dada de baja no puede quedar ocupada.
            if (!c.Activa)
                await cn.ExecuteAsync(
                    @"UPDATE dbo.PuestoCajaSesion SET FechaSalida = GETDATE(), Motivo = @motivo
                      WHERE CajaId = @Id AND FechaSalida IS NULL",
                    new { c.Id, motivo = MotivoSalidaCaja.Liberada });
        }
        return null;
    }

    // ------------------------------------------------------------------ sesiones

    /// <summary>La caja en la que el usuario está trabajando ahora (para seguir después de recargar la página).</summary>
    public async Task<PuestoCajaSesion> ActivaDeUsuarioAsync(string cs, Guid usuarioId)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.QueryFirstOrDefaultAsync<PuestoCajaSesion>(
            $@"SELECT TOP 1 s.*, c.Nombre AS Caja, c.Numero AS CajaNumero, c.SucursalId, c.MercadoPagoCajaId
               FROM dbo.PuestoCajaSesion s JOIN dbo.PuestoCaja c ON c.Id = s.CajaId
               WHERE s.UsuarioId = @usuarioId AND c.Activa = 1 AND {Viva}
               ORDER BY s.FechaIngreso DESC", new { usuarioId });
    }

    /// <summary>La última caja que usó el usuario en esa sucursal, para ofrecérsela primero.</summary>
    public async Task<Guid?> UltimaDeUsuarioAsync(string cs, Guid usuarioId, Guid sucursalId)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.ExecuteScalarAsync<Guid?>(
            @"SELECT TOP 1 s.CajaId
              FROM dbo.PuestoCajaSesion s JOIN dbo.PuestoCaja c ON c.Id = s.CajaId
              WHERE s.UsuarioId = @usuarioId AND c.SucursalId = @sucursalId AND c.Activa = 1
              ORDER BY s.FechaIngreso DESC", new { usuarioId, sucursalId });
    }

    /// <summary>
    /// El usuario toma la caja. Devuelve la sesión, o un mensaje si la caja la tiene otra persona.
    /// Todo va en una transacción con bloqueo sobre la caja, para que dos usuarios que eligen
    /// al mismo tiempo no queden los dos adentro.
    /// </summary>
    public async Task<(PuestoCajaSesion Sesion, string Error)> TomarAsync(string cs, Guid cajaId, Guid usuarioId)
    {
        using var cn = await Db.AbrirAsync(cs);
        using var tx = cn.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            var caja = await cn.QueryFirstOrDefaultAsync<PuestoCaja>(
                "SELECT * FROM dbo.PuestoCaja WITH (UPDLOCK, HOLDLOCK) WHERE Id = @cajaId", new { cajaId }, tx);
            if (caja == null || !caja.Activa) { tx.Rollback(); return (null, "Esa caja ya no está disponible."); }

            // Las sesiones sin aviso hace más de 10 minutos se cierran: la caja quedó libre.
            await cn.ExecuteAsync(
                @"UPDATE dbo.PuestoCajaSesion SET FechaSalida = UltimoLatido, Motivo = @motivo
                  WHERE FechaSalida IS NULL AND UltimoLatido < DATEADD(MINUTE, -10, GETDATE())",
                new { motivo = MotivoSalidaCaja.Inactividad }, tx);

            var ocupante = await cn.QueryFirstOrDefaultAsync<PuestoCajaSesion>(
                @"SELECT s.*, u.NombreUsuario AS Usuario FROM dbo.PuestoCajaSesion s
                  LEFT JOIN dbo.Usuario u ON u.Id = s.UsuarioId
                  WHERE s.CajaId = @cajaId AND s.FechaSalida IS NULL", new { cajaId }, tx);

            Guid sesionId;
            if (ocupante != null && ocupante.UsuarioId == usuarioId)
            {
                // Ya era suya (otra pestaña o recargó la página): sigue la misma sesión.
                sesionId = ocupante.Id;
                await cn.ExecuteAsync("UPDATE dbo.PuestoCajaSesion SET UltimoLatido = GETDATE() WHERE Id = @sesionId",
                    new { sesionId }, tx);
            }
            else if (ocupante != null)
            {
                tx.Rollback();
                return (null, $"{caja.Titulo} la está usando {ocupante.Usuario ?? "otro usuario"} desde las {ocupante.FechaIngreso:HH:mm}. Elegí otra.");
            }
            else
            {
                // Un usuario, una caja: si estaba en otra, se cierra.
                await cn.ExecuteAsync(
                    @"UPDATE dbo.PuestoCajaSesion SET FechaSalida = GETDATE(), Motivo = @motivo
                      WHERE UsuarioId = @usuarioId AND FechaSalida IS NULL",
                    new { usuarioId, motivo = MotivoSalidaCaja.Cambio }, tx);

                sesionId = Guid.NewGuid();
                await cn.ExecuteAsync(
                    "INSERT INTO dbo.PuestoCajaSesion (Id, CajaId, UsuarioId) VALUES (@sesionId, @cajaId, @usuarioId)",
                    new { sesionId, cajaId, usuarioId }, tx);
            }
            tx.Commit();

            var sesion = await cn.QueryFirstAsync<PuestoCajaSesion>(
                @"SELECT s.*, c.Nombre AS Caja, c.Numero AS CajaNumero, c.SucursalId, c.MercadoPagoCajaId
                  FROM dbo.PuestoCajaSesion s JOIN dbo.PuestoCaja c ON c.Id = s.CajaId
                  WHERE s.Id = @sesionId", new { sesionId });
            return (sesion, null);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627 or 1205)
        {
            // El índice de una sesión abierta por caja frenó a un segundo usuario (o hubo un bloqueo mutuo).
            try { tx.Rollback(); } catch { }
            return (null, "Otra persona tomó esa caja recién. Elegí otra.");
        }
    }

    /// <summary>
    /// Aviso de la pantalla de que el usuario sigue en la caja. Devuelve false si la sesión ya no
    /// está abierta (la liberó un supervisor, o venció por inactividad y la tomó otro).
    /// </summary>
    public async Task<bool> LatirAsync(string cs, Guid sesionId)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.ExecuteAsync(
            "UPDATE dbo.PuestoCajaSesion SET UltimoLatido = GETDATE() WHERE Id = @sesionId AND FechaSalida IS NULL",
            new { sesionId }) > 0;
    }

    /// <summary>El usuario salió del sistema: deja libre su caja.</summary>
    public async Task SalirAsync(string cs, Guid usuarioId)
    {
        using var cn = await Db.AbrirAsync(cs);
        await cn.ExecuteAsync(
            "UPDATE dbo.PuestoCajaSesion SET FechaSalida = GETDATE(), Motivo = @motivo WHERE UsuarioId = @usuarioId AND FechaSalida IS NULL",
            new { usuarioId, motivo = MotivoSalidaCaja.Salio });
    }

    /// <summary>Un supervisor o administrador libera una caja que quedó tomada.</summary>
    public async Task LiberarAsync(string cs, Guid sesionId, Guid porUsuarioId)
    {
        using var cn = await Db.AbrirAsync(cs);
        await cn.ExecuteAsync(
            @"UPDATE dbo.PuestoCajaSesion SET FechaSalida = GETDATE(), Motivo = @motivo, LiberadaPor = @porUsuarioId
              WHERE Id = @sesionId AND FechaSalida IS NULL",
            new { sesionId, porUsuarioId, motivo = MotivoSalidaCaja.Liberada });
    }

    /// <summary>
    /// Arqueo de una sucursal que no exige abrir caja pero tiene varias: UNA fila por caja con todo lo del día
    /// (desde las 00:00, lo haya hecho el cajero que sea mientras estuvo en ella), más una fila "Sin caja" por
    /// usuario con lo que facturó sin estar en ninguna. Cada fila sirve para <see cref="CajaService.ArqueoAsync"/>.
    /// </summary>
    /// <param name="puestoCajaId">Solo esa caja (y ninguna fila "Sin caja").</param>
    /// <param name="usuarioSinCaja">Solo la fila "Sin caja" de ese usuario (y ninguna caja).</param>
    public async Task<List<CajaTurno>> MovimientosPorCajaAsync(string cs, DateTime dia, Guid sucursalId, int puntoVenta,
        Guid? puestoCajaId = null, Guid? usuarioSinCaja = null)
    {
        var desde = dia.Date;
        var hasta = dia.Date.AddDays(1);
        using var cn = await Db.AbrirAsync(cs);
        var filas = new List<CajaTurno>();

        if (usuarioSinCaja == null)
        {
            var ventas = CajaService.EnLaCajaSql("f").Replace("@p", "c.Id");
            filas = (await cn.QueryAsync<CajaTurno>(
                $@"SELECT c.Id AS PuestoCajaId,
                          ISNULL(NULLIF(c.Nombre, ''), 'Caja ' + CAST(c.Numero AS VARCHAR(10))) AS Puesto,
                          STUFF((SELECT DISTINCT ', ' + u.NombreUsuario
                                 FROM dbo.PuestoCajaSesion s JOIN dbo.Usuario u ON u.Id = s.UsuarioId
                                 WHERE s.CajaId = c.Id AND s.FechaIngreso < @hasta AND ISNULL(s.FechaSalida, GETDATE()) >= @desde
                                 FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS Usuario,
                          CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.PuestoCajaSesion s WHERE s.CajaId = c.Id AND s.FechaSalida IS NULL
                                                 AND s.UltimoLatido >= DATEADD(MINUTE, -{MinutosInactividad}, GETDATE()))
                                    THEN 1 ELSE 0 END AS BIT) AS EnUso,
                          (SELECT ISNULL(SUM(f.MontoTotal), 0) FROM dbo.Factura f
                           WHERE f.Fecha >= @desde AND f.Fecha < @hasta AND ISNULL(f.Anulada, 0) = 0 AND {ventas}) AS Ventas
                   FROM dbo.PuestoCaja c
                   WHERE c.SucursalId = @sucursalId AND (@puestoCajaId IS NULL OR c.Id = @puestoCajaId)
                     AND EXISTS (SELECT 1 FROM dbo.PuestoCajaSesion s WHERE s.CajaId = c.Id
                                 AND s.FechaIngreso < @hasta AND ISNULL(s.FechaSalida, GETDATE()) >= @desde)
                   ORDER BY c.Numero",
                new { desde, hasta, sucursalId, puestoCajaId })).ToList();
            foreach (var f in filas)
            {
                f.EsDeCaja = true;   // el arqueo suma lo hecho en la caja, por cualquier cajero
                f.FechaApertura = desde;
                f.FechaCierre = hasta <= DateTime.Now ? hasta : (DateTime?)null;
            }
        }

        if (puestoCajaId == null)
        {
            // Lo facturado sin estar en ninguna caja (p. ej. un administrador que entró "sin caja").
            var fuera = (await cn.QueryAsync<CajaTurno>(
                $@"SELECT f.UsuarioId, u.NombreUsuario AS Usuario, SUM(f.MontoTotal) AS Ventas
                   FROM dbo.Factura f
                   LEFT JOIN dbo.Usuario u ON u.Id = f.UsuarioId
                   WHERE f.Fecha >= @desde AND f.Fecha < @hasta AND f.Terminal = @puntoVenta AND ISNULL(f.Anulada, 0) = 0
                     AND (@usuarioSinCaja IS NULL OR f.UsuarioId = @usuarioSinCaja)
                     AND NOT {CajaService.EnAlgunaCaja("f")}
                   GROUP BY f.UsuarioId, u.NombreUsuario",
                new { desde, hasta, puntoVenta, usuarioSinCaja })).ToList();
            foreach (var f in fuera)
            {
                f.FueraDeCaja = true;
                f.Puesto = "Sin caja";
                f.FechaApertura = desde;
                f.FechaCierre = hasta;
            }
            filas.AddRange(fuera);
        }

        foreach (var f in filas)
        {
            f.Id = Guid.Empty;
            f.SucursalId = sucursalId;
            f.MontoInicial = 0;
        }
        return filas;
    }

    /// <summary>
    /// Completa en cada turno de caja (arqueo) en qué caja de la sucursal estaba el usuario al abrirlo.
    /// </summary>
    public async Task AsignarPuestoAsync(string cs, List<CajaTurno> turnos, DateTime dia, Guid? sucursalId)
    {
        if (turnos.Count == 0) return;
        var sesiones = await HistorialAsync(cs, dia.AddDays(-1), dia, sucursalId);
        foreach (var t in turnos)
        {
            if (t.PuestoCajaId != null) continue;   // turno de la caja (script 28): ya sabe cuál es
            var s = sesiones
                .Where(x => x.UsuarioId == t.UsuarioId && x.FechaIngreso <= t.FechaApertura.AddMinutes(5)
                            && (x.FechaSalida ?? DateTime.MaxValue) >= t.FechaApertura)
                .OrderByDescending(x => x.FechaIngreso).FirstOrDefault()
                ?? sesiones.Where(x => x.UsuarioId == t.UsuarioId && x.FechaIngreso.Date == t.FechaApertura.Date)
                           .OrderBy(x => x.FechaIngreso).FirstOrDefault();
            if (s == null) continue;
            t.PuestoCajaId = s.CajaId;
            t.Puesto = s.TituloCaja;
        }
    }

    /// <summary>Historial de ingresos a las cajas, lo más nuevo primero (hasta 1000 filas).</summary>
    public async Task<List<PuestoCajaSesion>> HistorialAsync(string cs, DateTime desde, DateTime hasta,
        Guid? sucursalId = null, Guid? cajaId = null, Guid? usuarioId = null)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<PuestoCajaSesion>(
            @"SELECT TOP 1000 s.*, c.Nombre AS Caja, c.Numero AS CajaNumero, c.SucursalId, c.MercadoPagoCajaId,
                     u.NombreUsuario AS Usuario, l.NombreUsuario AS LiberadaPorUsuario
              FROM dbo.PuestoCajaSesion s
              JOIN dbo.PuestoCaja c ON c.Id = s.CajaId
              LEFT JOIN dbo.Usuario u ON u.Id = s.UsuarioId
              LEFT JOIN dbo.Usuario l ON l.Id = s.LiberadaPor
              WHERE s.FechaIngreso < @hastaEx AND ISNULL(s.FechaSalida, GETDATE()) >= @desde
                AND (@sucursalId IS NULL OR c.SucursalId = @sucursalId)
                AND (@cajaId IS NULL OR s.CajaId = @cajaId)
                AND (@usuarioId IS NULL OR s.UsuarioId = @usuarioId)
              ORDER BY s.FechaIngreso DESC",
            new { desde = desde.Date, hastaEx = hasta.Date.AddDays(1), sucursalId, cajaId, usuarioId })).ToList();
    }
}

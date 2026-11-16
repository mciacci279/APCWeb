using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Tenants;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Tablero de trabajo de CM Tech: pendientes, errores y pedidos, en columnas tipo Trello.
/// Vive en la base central y lo ven solo los usuarios de CM Tech.
/// </summary>
public class TicketService
{
    private readonly TenantRegistry _tenants;
    public TicketService(TenantRegistry tenants) => _tenants = tenants;

    private string Central => _tenants.Central;

    private const string Columnas = @"
        t.Id, t.Numero, t.Titulo, t.Detalle, t.Estado, t.Prioridad, t.Tipo, t.EmpresaId, t.AsignadoA,
        t.Vencimiento, t.Orden, t.FechaAlta, t.FechaCierre,
        e.Nombre AS Empresa, ua.NombreUsuario AS Asignado, uc.NombreUsuario AS CreadoPorNombre,
        (SELECT COUNT(*) FROM dbo.TicketNota n WHERE n.TicketId = t.Id) AS Notas";

    private const string Desde = @"
        FROM dbo.Ticket t
        LEFT JOIN dbo.Empresa e ON e.Id = t.EmpresaId
        LEFT JOIN dbo.Usuario ua ON ua.Id = t.AsignadoA
        LEFT JOIN dbo.Usuario uc ON uc.Id = t.CreadoPor";

    /// <summary>Las tarjetas del tablero. Los terminados se muestran solo los últimos días.</summary>
    public async Task<List<Ticket>> ListarAsync(Guid? empresaId = null, Guid? asignadoA = null, int diasListos = 14)
    {
        using var cn = await Db.AbrirAsync(Central);
        return (await cn.QueryAsync<Ticket>(
            $@"SELECT {Columnas} {Desde}
               WHERE (@empresaId IS NULL OR t.EmpresaId = @empresaId)
                 AND (@asignadoA IS NULL OR t.AsignadoA = @asignadoA)
                 AND (t.Estado <> 'LISTO' OR t.FechaCierre >= @desdeListos)
               ORDER BY t.Orden, t.FechaAlta DESC",
            new { empresaId, asignadoA, desdeListos = DateTime.Today.AddDays(-diasListos) })).ToList();
    }

    public async Task<Ticket> ObtenerAsync(Guid id)
    {
        using var cn = await Db.AbrirAsync(Central);
        return await cn.QueryFirstOrDefaultAsync<Ticket>($"SELECT {Columnas} {Desde} WHERE t.Id = @id", new { id });
    }

    public async Task<string> GuardarAsync(Ticket t, Guid usuarioId)
    {
        t.Titulo = t.Titulo?.Trim();
        if (string.IsNullOrWhiteSpace(t.Titulo)) return "Escribí un título.";
        if (t.Titulo.Length > 150) t.Titulo = t.Titulo[..150];

        using var cn = await Db.AbrirAsync(Central);
        if (t.EsNuevo)
        {
            t.Id = Guid.NewGuid();
            // Las tarjetas nuevas van arriba de su columna.
            t.Orden = (await cn.ExecuteScalarAsync<int?>(
                "SELECT MIN(Orden) FROM dbo.Ticket WHERE Estado = @Estado", new { t.Estado }) ?? 0) - 1;
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.Ticket (Id, Titulo, Detalle, Estado, Prioridad, Tipo, EmpresaId, AsignadoA, Vencimiento, Orden, CreadoPor)
                  VALUES (@Id, @Titulo, @Detalle, @Estado, @Prioridad, @Tipo, @EmpresaId, @AsignadoA, @Vencimiento, @Orden, @usuarioId)",
                new { t.Id, t.Titulo, t.Detalle, t.Estado, t.Prioridad, t.Tipo, t.EmpresaId, t.AsignadoA, t.Vencimiento, t.Orden, usuarioId });
        }
        else
        {
            await cn.ExecuteAsync(
                @"UPDATE dbo.Ticket SET Titulo = @Titulo, Detalle = @Detalle, Prioridad = @Prioridad, Tipo = @Tipo,
                         EmpresaId = @EmpresaId, AsignadoA = @AsignadoA, Vencimiento = @Vencimiento
                  WHERE Id = @Id",
                new { t.Id, t.Titulo, t.Detalle, t.Prioridad, t.Tipo, t.EmpresaId, t.AsignadoA, t.Vencimiento });
        }
        return null;
    }

    /// <summary>Mueve la tarjeta a otra columna. Al llegar a «Listo» queda la fecha de cierre.</summary>
    public async Task MoverAsync(Guid id, string estado, Guid usuarioId)
    {
        using var cn = await Db.AbrirAsync(Central);
        var orden = (await cn.ExecuteScalarAsync<int?>(
            "SELECT MIN(Orden) FROM dbo.Ticket WHERE Estado = @estado", new { estado }) ?? 0) - 1;
        await cn.ExecuteAsync(
            @"UPDATE dbo.Ticket SET Estado = @estado, Orden = @orden,
                     FechaCierre = CASE WHEN @estado = 'LISTO' THEN GETDATE() ELSE NULL END,
                     CerradoPor  = CASE WHEN @estado = 'LISTO' THEN @usuarioId ELSE NULL END
              WHERE Id = @id", new { id, estado, orden, usuarioId });
    }

    /// <summary>Sube o baja la tarjeta dentro de su columna.</summary>
    public async Task ReordenarAsync(Guid id, bool subir)
    {
        using var cn = await Db.AbrirAsync(Central);
        var actual = await cn.QueryFirstOrDefaultAsync<(string Estado, int Orden)>(
            "SELECT Estado, Orden FROM dbo.Ticket WHERE Id = @id", new { id });
        if (actual == default) return;

        var vecino = await cn.QueryFirstOrDefaultAsync<(Guid Id, int Orden)>(
            subir
                ? "SELECT TOP 1 Id, Orden FROM dbo.Ticket WHERE Estado = @Estado AND Orden < @Orden ORDER BY Orden DESC"
                : "SELECT TOP 1 Id, Orden FROM dbo.Ticket WHERE Estado = @Estado AND Orden > @Orden ORDER BY Orden",
            new { actual.Estado, actual.Orden });
        if (vecino == default) return;

        await cn.ExecuteAsync("UPDATE dbo.Ticket SET Orden = @o WHERE Id = @id",
            new[]
            {
                new { id, o = vecino.Orden },
                new { id = vecino.Id, o = actual.Orden }
            });
    }

    public async Task EliminarAsync(Guid id)
    {
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync("DELETE FROM dbo.TicketNota WHERE TicketId = @id; DELETE FROM dbo.Ticket WHERE Id = @id;", new { id });
    }

    // ------------------------------------------------------------------ notas

    public async Task<List<TicketNota>> NotasAsync(Guid ticketId)
    {
        using var cn = await Db.AbrirAsync(Central);
        return (await cn.QueryAsync<TicketNota>(
            @"SELECT n.Id, n.TicketId, n.Fecha, n.Texto, u.NombreUsuario AS Usuario
              FROM dbo.TicketNota n LEFT JOIN dbo.Usuario u ON u.Id = n.UsuarioId
              WHERE n.TicketId = @ticketId ORDER BY n.Fecha DESC", new { ticketId })).ToList();
    }

    public async Task<string> AgregarNotaAsync(Guid ticketId, Guid usuarioId, string texto)
    {
        texto = texto?.Trim();
        if (string.IsNullOrWhiteSpace(texto)) return "Escribí algo.";
        if (texto.Length > 1000) texto = texto[..1000];

        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync(
            @"INSERT INTO dbo.TicketNota (Id, TicketId, Fecha, UsuarioId, Texto)
              VALUES (NEWID(), @ticketId, GETDATE(), @usuarioId, @texto)",
            new { ticketId, usuarioId, texto });
        return null;
    }

    /// <summary>Usuarios de CM Tech, para asignar las tarjetas.</summary>
    public async Task<List<OpcionItem>> UsuariosCmTechAsync()
    {
        using var cn = await Db.AbrirAsync(Central);
        return (await cn.QueryAsync<OpcionItem>(
            @"SELECT Id, NombreUsuario AS Nombre FROM dbo.Usuario
              WHERE EmpresaId IS NULL AND Activo = 1 ORDER BY NombreUsuario")).ToList();
    }
}

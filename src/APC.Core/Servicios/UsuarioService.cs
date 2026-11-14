using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Seguridad;
using APC.Core.Tenants;
using Dapper;
using Microsoft.Data.SqlClient;

namespace APC.Core.Servicios;

public class IngresoRegistro
{
    public DateTime Fecha { get; set; }
    public string EmpresaCodigo { get; set; }
    public string NombreUsuario { get; set; }
    public bool Exito { get; set; }
    public string Motivo { get; set; }
    public string Ip { get; set; }
}

public enum EstadoIngreso { Ok, EmpresaNoExiste, EmpresaSuspendida, ClaveIncorrecta, Bloqueado }

public class IngresoResultado
{
    public EstadoIngreso Estado { get; init; }
    public UsuarioSesion Usuario { get; init; }
    public TenantConfig Tenant { get; init; }
}

/// <summary>
/// Usuarios web en la base central (APCWeb_Central). Cada usuario de una empresa tiene además
/// una fila "espejo" en dbo.Usuario de la base de la empresa, con el mismo Id y sin clave,
/// para que Factura, Caja y CajaTurno sigan apuntando a un usuario existente.
/// </summary>
public class UsuarioService
{
    private const int MaxIntentos = 5;
    private static readonly TimeSpan DuracionBloqueo = TimeSpan.FromMinutes(15);
    // Se usa para tardar lo mismo cuando el usuario no existe (no revela qué usuarios existen)
    private static readonly string HashFicticio = HashClave.Crear(Guid.NewGuid().ToString());

    private readonly TenantRegistry _tenants;

    public UsuarioService(TenantRegistry tenants) => _tenants = tenants;

    private string Central => _tenants.Central;

    // ------------------------------------------------------------------ ingreso

    public async Task<IngresoResultado> ValidarAsync(string empresa, string usuario, string clave, string ip)
    {
        empresa = (empresa ?? "").Trim().ToLowerInvariant();
        usuario = (usuario ?? "").Trim();
        bool esCmTech = empresa == RolUsuario.CodigoCmTech;

        TenantConfig tenant = null;
        if (!esCmTech)
        {
            tenant = _tenants.BuscarCualquiera(empresa);
            if (tenant == null)
            {
                await LogAsync(empresa, usuario, null, false, "Empresa inexistente", ip);
                return new IngresoResultado { Estado = EstadoIngreso.EmpresaNoExiste };
            }
            if (!tenant.Activa)
            {
                await LogAsync(empresa, usuario, null, false, "Empresa suspendida", ip);
                return new IngresoResultado { Estado = EstadoIngreso.EmpresaSuspendida };
            }
        }

        using var cn = await Db.AbrirAsync(Central);
        var u = await cn.QueryFirstOrDefaultAsync<UsuarioSesion>(
            @"SELECT Id, NombreUsuario, Nombre, Apellido, EmpresaId, Rol, Activo, ClaveHash, IntentosFallidos, BloqueadoHasta, Email
              FROM dbo.Usuario
              WHERE NombreUsuario = @usuario
                AND ((@cmtech = 1 AND EmpresaId IS NULL AND Rol = 'SuperAdmin')
                  OR (@cmtech = 0 AND EmpresaId = @emp))",
            new { usuario, cmtech = esCmTech, emp = tenant?.Id });

        if (u == null || !u.Activo)
        {
            HashClave.Verificar(clave ?? "", HashFicticio);
            await LogAsync(empresa, usuario, u?.Id, false, u == null ? "Usuario inexistente" : "Usuario inactivo", ip);
            return new IngresoResultado { Estado = EstadoIngreso.ClaveIncorrecta };
        }

        if (u.BloqueadoHasta > DateTime.Now)
        {
            await LogAsync(empresa, usuario, u.Id, false, "Usuario bloqueado", ip);
            return new IngresoResultado { Estado = EstadoIngreso.Bloqueado };
        }

        if (!HashClave.Verificar(clave ?? "", u.ClaveHash))
        {
            var intentos = u.IntentosFallidos + 1;
            var bloquear = intentos >= MaxIntentos;
            await cn.ExecuteAsync(
                "UPDATE dbo.Usuario SET IntentosFallidos = @i, BloqueadoHasta = @b WHERE Id = @id",
                new { i = bloquear ? 0 : intentos, b = bloquear ? DateTime.Now.Add(DuracionBloqueo) : (DateTime?)null, id = u.Id });
            await LogAsync(empresa, usuario, u.Id, false, bloquear ? "Clave incorrecta: bloqueado 15 min" : "Clave incorrecta", ip);
            return new IngresoResultado { Estado = bloquear ? EstadoIngreso.Bloqueado : EstadoIngreso.ClaveIncorrecta };
        }

        await cn.ExecuteAsync(
            "UPDATE dbo.Usuario SET IntentosFallidos = 0, BloqueadoHasta = NULL, UltimoIngreso = GETDATE() WHERE Id = @id",
            new { id = u.Id });
        await LogAsync(empresa, usuario, u.Id, true, null, ip);
        return new IngresoResultado { Estado = EstadoIngreso.Ok, Usuario = u, Tenant = tenant };
    }

    // ------------------------------------------------------------------ instalación (CM Tech)

    public async Task<bool> ExisteSuperAdminAsync()
    {
        using var cn = await Db.AbrirAsync(Central);
        return await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Usuario WHERE Rol = 'SuperAdmin'") > 0;
    }

    public async Task<string> CrearSuperAdminAsync(string usuario, string nombre, string clave)
    {
        var err = ValidarNombreUsuario(usuario) ?? ValidarClave(clave);
        if (err != null) return err;
        if (await ExisteSuperAdminAsync()) return "La instalación ya se hizo: ya existe un usuario de CM Tech.";

        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync(
            @"INSERT INTO dbo.Usuario (Id, EmpresaId, NombreUsuario, Nombre, ClaveHash, Rol)
              VALUES (NEWID(), NULL, @usuario, @nombre, @hash, 'SuperAdmin')",
            new { usuario = usuario.Trim(), nombre, hash = HashClave.Crear(clave) });
        return null;
    }

    // ------------------------------------------------------------------ usuarios de una empresa

    public async Task<List<UsuarioListado>> ListarAsync(Guid empresaId)
    {
        using var cn = await Db.AbrirAsync(Central);
        return (await cn.QueryAsync<UsuarioListado>(
            @"SELECT Id, NombreUsuario, Nombre, Apellido, Email, Rol, Activo, UltimoIngreso, BloqueadoHasta
              FROM dbo.Usuario WHERE EmpresaId = @empresaId
              ORDER BY CASE Rol WHEN 'Admin' THEN 0 ELSE 1 END, NombreUsuario",
            new { empresaId })).ToList();
    }

    public async Task<string> CrearAsync(TenantConfig tenant, string usuario, string nombre, string apellido,
        string email, string clave, string rol)
    {
        var err = ValidarNombreUsuario(usuario) ?? ValidarClave(clave);
        if (err != null) return err;
        if (rol != RolUsuario.Admin && rol != RolUsuario.Empleado) return "Rol inválido.";
        usuario = usuario.Trim();

        using var cn = await Db.AbrirAsync(Central);
        var existe = await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Usuario WHERE EmpresaId = @emp AND NombreUsuario = @usuario",
            new { emp = tenant.Id, usuario });
        if (existe > 0) return "Ya existe un usuario con ese nombre en esta empresa.";

        using var tx = cn.BeginTransaction();
        var id = Guid.NewGuid();
        await cn.ExecuteAsync(
            @"INSERT INTO dbo.Usuario (Id, EmpresaId, NombreUsuario, Nombre, Apellido, Email, ClaveHash, Rol)
              VALUES (@id, @emp, @usuario, @nombre, @apellido, @email, @hash, @rol)",
            new
            {
                id, emp = tenant.Id, usuario, nombre = Limpio(nombre), apellido = Limpio(apellido),
                email = Limpio(email), hash = HashClave.Crear(clave), rol
            }, tx);

        try
        {
            await CrearEspejoAsync(tenant, id, usuario, Limpio(nombre), Limpio(apellido));
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            tx.Rollback();
            return "La base de la empresa ya tiene un usuario con ese nombre. Elegí otro.";
        }
        catch (Exception ex)
        {
            tx.Rollback();
            return "No se pudo registrar el usuario en la base de la empresa: " + ex.GetBaseException().Message;
        }

        tx.Commit();
        return null;
    }

    /// <summary>Cambia la clave y desbloquea. empresaId = null solo para usuarios de CM Tech.</summary>
    public async Task<string> CambiarClaveAsync(Guid usuarioId, Guid? empresaId, string claveNueva)
    {
        var err = ValidarClave(claveNueva);
        if (err != null) return err;
        using var cn = await Db.AbrirAsync(Central);
        var filas = await cn.ExecuteAsync(
            @"UPDATE dbo.Usuario SET ClaveHash = @hash, IntentosFallidos = 0, BloqueadoHasta = NULL
              WHERE Id = @id AND ((@emp IS NULL AND EmpresaId IS NULL) OR EmpresaId = @emp)",
            new { hash = HashClave.Crear(claveNueva), id = usuarioId, emp = empresaId });
        return filas == 0 ? "No se encontró el usuario." : null;
    }

    public async Task<string> CambiarRolAsync(Guid usuarioId, Guid empresaId, string rol, Guid usuarioActual)
    {
        if (rol != RolUsuario.Admin && rol != RolUsuario.Empleado) return "Rol inválido.";
        if (usuarioId == usuarioActual && rol != RolUsuario.Admin) return "No podés quitarte a vos mismo el rol de administrador.";
        using var cn = await Db.AbrirAsync(Central);
        if (rol == RolUsuario.Empleado && await OtrosAdminsActivosAsync(cn, empresaId, usuarioId) == 0)
            return "La empresa tiene que tener al menos un administrador activo.";
        var filas = await cn.ExecuteAsync(
            "UPDATE dbo.Usuario SET Rol = @rol WHERE Id = @id AND EmpresaId = @emp",
            new { rol, id = usuarioId, emp = empresaId });
        return filas == 0 ? "No se encontró el usuario." : null;
    }

    public async Task<string> CambiarActivoAsync(Guid usuarioId, Guid empresaId, bool activo, Guid usuarioActual)
    {
        if (usuarioId == usuarioActual && !activo) return "No podés darte de baja a vos mismo.";
        using var cn = await Db.AbrirAsync(Central);
        if (!activo)
        {
            var rol = await cn.ExecuteScalarAsync<string>(
                "SELECT Rol FROM dbo.Usuario WHERE Id = @id AND EmpresaId = @emp", new { id = usuarioId, emp = empresaId });
            if (rol == RolUsuario.Admin && await OtrosAdminsActivosAsync(cn, empresaId, usuarioId) == 0)
                return "La empresa tiene que tener al menos un administrador activo.";
        }
        var filas = await cn.ExecuteAsync(
            "UPDATE dbo.Usuario SET Activo = @activo WHERE Id = @id AND EmpresaId = @emp",
            new { activo, id = usuarioId, emp = empresaId });
        return filas == 0 ? "No se encontró el usuario." : null;
    }

    public async Task<string> CambiarEmailAsync(Guid usuarioId, Guid empresaId, string email)
    {
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (email != null && (email.Length > 150 || !email.Contains('@') || email.Contains(' ')))
            return "El email no es válido.";
        using var cn = await Db.AbrirAsync(Central);
        var filas = await cn.ExecuteAsync(
            "UPDATE dbo.Usuario SET Email = @email WHERE Id = @id AND EmpresaId = @emp",
            new { email, id = usuarioId, emp = empresaId });
        return filas == 0 ? "No se encontró el usuario." : null;
    }

    /// <summary>Registro de ingresos. empresaCodigo null = todas las empresas (solo panel de CM Tech).</summary>
    public async Task<List<IngresoRegistro>> IngresosAsync(string empresaCodigo, DateTime desde, DateTime hasta,
        bool soloFallidos, int max = 500)
    {
        using var cn = await Db.AbrirAsync(Central);
        return (await cn.QueryAsync<IngresoRegistro>(
            @"SELECT TOP (@max) Fecha, EmpresaCodigo, NombreUsuario, Exito, Motivo, Ip
              FROM dbo.IngresoLog
              WHERE Fecha >= @desde AND Fecha < @hasta
                AND (@empresa IS NULL OR EmpresaCodigo = @empresa)
                AND (@soloFallidos = 0 OR Exito = 0)
              ORDER BY Fecha DESC",
            new { max, desde = desde.Date, hasta = hasta.Date.AddDays(1), empresa = empresaCodigo, soloFallidos })).ToList();
    }

    // ------------------------------------------------------------------ auxiliares

    private static async Task CrearEspejoAsync(TenantConfig tenant, Guid id, string usuario, string nombre, string apellido)
    {
        using var cn = await Db.AbrirAsync(tenant.ConnectionString);
        var empresaCliente = await cn.ExecuteScalarAsync<Guid?>("SELECT TOP 1 Id FROM dbo.Empresa");
        if (empresaCliente == null)
            throw new InvalidOperationException("la base no tiene datos de empresa cargados (tabla Empresa vacía)");
        await cn.ExecuteAsync(
            @"INSERT INTO dbo.Usuario (Id, Nombre, Apellido, NombreUsuario, Pass, EmpresaID)
              VALUES (@id, @nombre, @apellido, @usuario, NULL, @emp)",
            new { id, nombre, apellido, usuario, emp = empresaCliente });
    }

    private static Task<int> OtrosAdminsActivosAsync(System.Data.IDbConnection cn, Guid empresaId, Guid excepto) =>
        cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Usuario WHERE EmpresaId = @emp AND Rol = 'Admin' AND Activo = 1 AND Id <> @id",
            new { emp = empresaId, id = excepto });

    /// <summary>Registra un evento de acceso (ingreso, recupero de clave) en dbo.IngresoLog.</summary>
    public Task RegistrarEventoAsync(string empresa, string usuario, Guid? usuarioId, bool exito, string motivo, string ip) =>
        LogAsync(empresa, usuario, usuarioId, exito, motivo, ip);

    private async Task LogAsync(string empresa, string usuario, Guid? usuarioId, bool exito, string motivo, string ip)
    {
        try
        {
            using var cn = await Db.AbrirAsync(Central);
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.IngresoLog (EmpresaCodigo, NombreUsuario, UsuarioId, Exito, Motivo, Ip)
                  VALUES (@empresa, @usuario, @usuarioId, @exito, @motivo, @ip)",
                new { empresa = Corta(empresa, 30), usuario = Corta(usuario, 50), usuarioId, exito, motivo = Corta(motivo, 100), ip = Corta(ip, 50) });
        }
        catch
        {
            // El registro de ingresos nunca debe impedir el ingreso
        }
    }

    private static string ValidarNombreUsuario(string usuario)
    {
        if (string.IsNullOrWhiteSpace(usuario)) return "Falta el nombre de usuario.";
        if (usuario.Trim().Length > 50) return "El nombre de usuario no puede superar los 50 caracteres.";
        return null;
    }

    private static string ValidarClave(string clave)
    {
        if (string.IsNullOrEmpty(clave) || clave.Length < 6) return "La clave debe tener al menos 6 caracteres.";
        if (clave.Length > 100) return "La clave no puede superar los 100 caracteres.";
        return null;
    }

    private static string Limpio(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Corta(string s, int max) => s == null ? null : (s.Length > max ? s[..max] : s);
}

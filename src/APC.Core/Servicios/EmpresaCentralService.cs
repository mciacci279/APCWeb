using System.Text.RegularExpressions;
using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Tenants;
using Dapper;
using Microsoft.Data.SqlClient;

namespace APC.Core.Servicios;

/// <summary>Alta, edición y suspensión de empresas en la base central (panel de CM Tech).</summary>
public class EmpresaCentralService
{
    private static readonly Regex CodigoValido = new("^[a-z0-9][a-z0-9-]{1,29}$");
    private readonly TenantRegistry _tenants;

    public const int LogoMaximoBytes = 500 * 1024;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _logos = new();

    public EmpresaCentralService(TenantRegistry tenants) => _tenants = tenants;

    // ------------------------------------------------------------------ logo

    /// <summary>Guarda el logo de la empresa. Solo PNG o JPG (se verifica el contenido, no la extensión).</summary>
    public async Task<string> GuardarLogoAsync(Guid empresaId, byte[] datos)
    {
        if (datos == null || datos.Length == 0) return "El archivo está vacío.";
        if (datos.Length > LogoMaximoBytes) return "El logo no puede superar los 500 KB. Achicá la imagen.";
        var tipo = TipoImagen(datos);
        if (tipo == null) return "El logo tiene que ser una imagen PNG o JPG.";

        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync("UPDATE dbo.Empresa SET Logo = @datos, LogoTipo = @tipo WHERE Id = @id",
            new { datos, tipo, id = empresaId });
        _logos.TryRemove(empresaId, out _);
        return null;
    }

    public async Task QuitarLogoAsync(Guid empresaId)
    {
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync("UPDATE dbo.Empresa SET Logo = NULL, LogoTipo = NULL WHERE Id = @id", new { id = empresaId });
        _logos.TryRemove(empresaId, out _);
    }

    /// <summary>Logo como data URI para insertar en la página de impresión (null si no tiene). Se cachea en memoria.</summary>
    public async Task<string> LogoDataUriAsync(Guid empresaId)
    {
        if (_logos.TryGetValue(empresaId, out var cache)) return cache == "" ? null : cache;
        using var cn = await Db.AbrirAsync(Central);
        var fila = await cn.QueryFirstOrDefaultAsync<(byte[] Logo, string LogoTipo)>(
            "SELECT Logo, LogoTipo FROM dbo.Empresa WHERE Id = @id", new { id = empresaId });
        var uri = fila.Logo == null || fila.Logo.Length == 0
            ? ""
            : $"data:{fila.LogoTipo ?? "image/png"};base64,{Convert.ToBase64String(fila.Logo)}";
        _logos[empresaId] = uri;
        return uri == "" ? null : uri;
    }

    private static string TipoImagen(byte[] d)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        return null;
    }

    private string Central => _tenants.Central;

    public async Task<List<TenantConfig>> ListarAsync()
    {
        using var cn = await Db.AbrirAsync(Central);
        return (await cn.QueryAsync<TenantConfig>(
            @"SELECT e.Id, e.Codigo, e.Nombre, e.NombreFantasia, e.ConnectionString, e.PuntoVenta, e.Ambiente, e.CertificadoPath, e.ClavePath,
                     e.Concepto, e.Moneda, e.Activa, e.MotivoSuspension, e.FechaAlta, e.LeyendaTicket,
                     (SELECT COUNT(*) FROM dbo.Usuario u WHERE u.EmpresaId = e.Id AND u.Activo = 1) AS CantidadUsuarios,
                     CAST(CASE WHEN e.Logo IS NULL THEN 0 ELSE 1 END AS BIT) AS TieneLogo
              FROM dbo.Empresa e ORDER BY e.Nombre")).ToList();
    }

    /// <summary>Crea (Id vacío) o actualiza una empresa. El código no se puede cambiar después del alta.</summary>
    public async Task<string> GuardarAsync(TenantConfig e)
    {
        bool nueva = e.Id == Guid.Empty;
        e.Codigo = e.Codigo?.Trim().ToLowerInvariant();
        e.Nombre = e.Nombre?.Trim();
        e.NombreFantasia = string.IsNullOrWhiteSpace(e.NombreFantasia) ? null : e.NombreFantasia.Trim();
        e.ConnectionString = NormalizarConexion(e.ConnectionString);
        e.Ambiente = string.IsNullOrWhiteSpace(e.Ambiente) ? "HOMO" : e.Ambiente.Trim().ToUpperInvariant();
        e.Moneda = string.IsNullOrWhiteSpace(e.Moneda) ? "PES" : e.Moneda.Trim().ToUpperInvariant();
        e.CertificadoPath = string.IsNullOrWhiteSpace(e.CertificadoPath) ? null : e.CertificadoPath.Trim();
        e.ClavePath = string.IsNullOrWhiteSpace(e.ClavePath) ? null : e.ClavePath.Trim();
        e.LeyendaTicket = (e.LeyendaTicket ?? "").Trim();
        if (e.LeyendaTicket.Length > 200) return "La leyenda del ticket no puede superar los 200 caracteres.";

        if (nueva && (e.Codigo == null || !CodigoValido.IsMatch(e.Codigo)))
            return "El código debe tener entre 2 y 30 caracteres: minúsculas, números o guiones.";
        if (nueva && e.Codigo == RolUsuario.CodigoCmTech) return "Ese código está reservado.";
        if (string.IsNullOrWhiteSpace(e.Nombre)) return "Falta la razón social de la empresa.";
        if (string.IsNullOrWhiteSpace(e.ConnectionString)) return "Falta la conexión a la base de datos.";
        if (e.PuntoVenta < 1 || e.PuntoVenta > 99999) return "El punto de venta debe estar entre 1 y 99999.";
        if (e.Ambiente != "HOMO" && e.Ambiente != "PROD") return "Ambiente inválido.";
        if (e.Concepto < 1 || e.Concepto > 3) return "Concepto inválido.";
        if (e.Moneda.Length != 3) return "La moneda debe tener 3 letras (ej. PES).";
        if (e.CertificadoPath == null || e.ClavePath == null) return "Faltan las rutas del certificado y la clave de ARCA.";

        using var cn = await Db.AbrirAsync(Central);
        if (nueva)
        {
            var existe = await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Empresa WHERE Codigo = @c", new { c = e.Codigo });
            if (existe > 0) return "Ya existe una empresa con ese código.";
            e.Id = Guid.NewGuid();
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.Empresa (Id, Codigo, Nombre, NombreFantasia, ConnectionString, PuntoVenta, Ambiente, CertificadoPath, ClavePath, Concepto, Moneda, LeyendaTicket, Activa)
                  VALUES (@Id, @Codigo, @Nombre, @NombreFantasia, @ConnectionString, @PuntoVenta, @Ambiente, @CertificadoPath, @ClavePath, @Concepto, @Moneda, @LeyendaTicket, 1)", e);
        }
        else
        {
            await cn.ExecuteAsync(
                @"UPDATE dbo.Empresa SET Nombre = @Nombre, NombreFantasia = @NombreFantasia, ConnectionString = @ConnectionString, PuntoVenta = @PuntoVenta,
                         Ambiente = @Ambiente, CertificadoPath = @CertificadoPath, ClavePath = @ClavePath,
                         Concepto = @Concepto, Moneda = @Moneda, LeyendaTicket = @LeyendaTicket
                  WHERE Id = @Id", e);
        }
        _tenants.Invalidar();
        return null;
    }

    public async Task<string> CambiarEstadoAsync(Guid id, bool activa, string motivo)
    {
        if (!activa && string.IsNullOrWhiteSpace(motivo)) return "Indicá el motivo de la suspensión.";
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync(
            "UPDATE dbo.Empresa SET Activa = @activa, MotivoSuspension = @motivo WHERE Id = @id",
            new { id, activa, motivo = activa ? null : motivo.Trim() });
        _tenants.Invalidar();
        return null;
    }

    /// <summary>
    /// Limpia lo que suele quedar al copiar una conexión de un archivo JSON o de un App.config:
    /// comillas alrededor, "connectionString=", barras invertidas dobles, espacios y saltos de línea.
    /// </summary>
    public static string NormalizarConexion(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return cs;
        cs = cs.Replace("\r", "").Replace("\n", " ").Trim();
        if (cs.StartsWith("connectionString=", StringComparison.OrdinalIgnoreCase)) cs = cs["connectionString=".Length..];
        cs = cs.Trim().TrimEnd('/', '>').Trim().Trim('"', '\'').Trim();
        cs = cs.Replace("\\\\", "\\");
        return cs;
    }

    /// <summary>Prueba la conexión y verifica que la base sea de APC y tenga la tabla CajaTurno.</summary>
    public async Task<(bool Ok, string Mensaje)> ProbarConexionAsync(string connectionString)
    {
        try
        {
            using var cn = new SqlConnection(NormalizarConexion(connectionString));
            await cn.OpenAsync();
            var emp = await cn.QueryFirstOrDefaultAsync<(string Nombre, string Cuit)>("SELECT TOP 1 Nombre, Cuit FROM dbo.Empresa");
            var tieneCaja = await cn.ExecuteScalarAsync<int>(
                "SELECT CASE WHEN OBJECT_ID(N'dbo.CajaTurno', N'U') IS NULL THEN 0 ELSE 1 END") == 1;

            if (emp.Nombre == null) return (false, "Conecta, pero la tabla Empresa está vacía: cargá los datos fiscales de la empresa.");
            var texto = $"Conexión correcta: {emp.Nombre} · CUIT {emp.Cuit}.";
            return tieneCaja
                ? (true, texto)
                : (false, texto + " Falta ejecutar el script 01_apcweb_cajaturno.sql en esta base.");
        }
        catch (Exception ex)
        {
            return (false, "No se pudo conectar: " + ex.GetBaseException().Message);
        }
    }
}

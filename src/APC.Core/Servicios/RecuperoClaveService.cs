using System.Net;
using System.Security.Cryptography;
using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Tenants;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Recupero de clave por email: link de un solo uso, vence a los 30 minutos, se guarda solo su hash.
/// La respuesta al usuario es siempre la misma, exista o no la cuenta (no revela qué usuarios existen).
/// </summary>
public class RecuperoClaveService
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(30);
    private const int MaxPedidosPorHora = 3;

    private readonly TenantRegistry _tenants;
    private readonly EmailService _email;
    private readonly UsuarioService _usuarios;

    public RecuperoClaveService(TenantRegistry tenants, EmailService email, UsuarioService usuarios)
    {
        _tenants = tenants;
        _email = email;
        _usuarios = usuarios;
    }

    public bool EmailConfigurado => _email.Configurado;

    private class UsuarioRecupero
    {
        public Guid Id { get; set; }
        public Guid? EmpresaId { get; set; }
        public string NombreUsuario { get; set; }
        public string Nombre { get; set; }
        public string Email { get; set; }
        public bool Activo { get; set; }
    }

    public async Task SolicitarAsync(string empresa, string usuario, string urlBase, string ip)
    {
        empresa = (empresa ?? "").Trim().ToLowerInvariant();
        usuario = (usuario ?? "").Trim();
        bool esCmTech = empresa == RolUsuario.CodigoCmTech;
        TenantConfig tenant = null;
        if (!esCmTech)
        {
            tenant = _tenants.Buscar(empresa);
            if (tenant == null) { await Log(empresa, usuario, null, false, "Recupero: empresa inexistente o suspendida", ip); return; }
        }

        using var cn = await Db.AbrirAsync(_tenants.Central);
        var u = await cn.QueryFirstOrDefaultAsync<UsuarioRecupero>(
            @"SELECT Id, EmpresaId, NombreUsuario, Nombre, Email, Activo FROM dbo.Usuario
              WHERE NombreUsuario = @usuario
                AND ((@cmtech = 1 AND EmpresaId IS NULL AND Rol = 'SuperAdmin') OR (@cmtech = 0 AND EmpresaId = @emp))",
            new { usuario, cmtech = esCmTech, emp = tenant?.Id });

        if (u == null || !u.Activo) { await Log(empresa, usuario, u?.Id, false, "Recupero: usuario inexistente o inactivo", ip); return; }
        if (string.IsNullOrWhiteSpace(u.Email)) { await Log(empresa, usuario, u.Id, false, "Recupero: el usuario no tiene email", ip); return; }

        var recientes = await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.RecuperoClave WHERE UsuarioId = @id AND Fecha > DATEADD(HOUR, -1, GETDATE())", new { id = u.Id });
        if (recientes >= MaxPedidosPorHora) { await Log(empresa, usuario, u.Id, false, "Recupero: demasiados pedidos en una hora", ip); return; }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        await cn.ExecuteAsync(
            @"INSERT INTO dbo.RecuperoClave (Id, UsuarioId, TokenHash, Vence, Ip) VALUES (NEWID(), @id, @hash, @vence, @ip)",
            new { id = u.Id, hash = Hash(token), vence = DateTime.Now.Add(Vigencia), ip = Corta(ip, 50) });

        var link = urlBase.TrimEnd('/') + "/restablecer?t=" + token;
        var nombre = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(u.Nombre) ? u.NombreUsuario : u.Nombre);
        var empresaTexto = WebUtility.HtmlEncode(esCmTech ? "CM Tech" : tenant.Nombre);
        var html = $@"<div style=""font-family:Arial,Helvetica,sans-serif;font-size:15px;color:#16202C;max-width:520px"">
<p>Hola {nombre}:</p>
<p>Pediste restablecer tu clave de <strong>APC Web</strong> ({empresaTexto}, usuario <strong>{WebUtility.HtmlEncode(u.NombreUsuario)}</strong>).</p>
<p><a href=""{WebUtility.HtmlEncode(link)}"" style=""display:inline-block;background:#0F2A4A;color:#fff;padding:12px 20px;border-radius:8px;text-decoration:none;font-weight:bold"">Elegir una clave nueva</a></p>
<p style=""color:#4A5868;font-size:13px"">El link sirve una sola vez y vence en 30 minutos. Si no fuiste vos, ignorá este email: tu clave sigue igual.</p>
</div>";
        var texto = $"Hola {u.Nombre ?? u.NombreUsuario}:\n\nPara elegir una clave nueva de APC Web entrá a:\n{link}\n\n" +
                    "El link sirve una sola vez y vence en 30 minutos. Si no fuiste vos, ignorá este email.";

        try
        {
            await _email.EnviarAsync(u.Email, u.Nombre, "APC Web · Restablecer tu clave", html, texto);
            await Log(empresa, usuario, u.Id, true, "Recupero: email enviado", ip);
        }
        catch (Exception ex)
        {
            await Log(empresa, usuario, u.Id, false, "Recupero: no se pudo enviar el email", ip);
            throw new InvalidOperationException("No se pudo enviar el email: " + ex.GetBaseException().Message, ex);
        }
    }

    /// <summary>Si el link sigue vigente, devuelve el nombre de usuario (para mostrarlo); si no, null.</summary>
    public async Task<string> ValidarTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var cn = await Db.AbrirAsync(_tenants.Central);
        return await cn.QueryFirstOrDefaultAsync<string>(
            @"SELECT u.NombreUsuario FROM dbo.RecuperoClave r JOIN dbo.Usuario u ON u.Id = r.UsuarioId
              WHERE r.TokenHash = @hash AND r.Usado = 0 AND r.Vence > GETDATE() AND u.Activo = 1",
            new { hash = Hash(token) });
    }

    /// <summary>Cambia la clave con un link vigente. Devuelve (error, código de empresa para el ingreso).</summary>
    public async Task<(string Error, string Empresa)> RestablecerAsync(string token, string claveNueva, string ip)
    {
        if (string.IsNullOrWhiteSpace(token)) return ("El link no es válido.", null);
        using var cn = await Db.AbrirAsync(_tenants.Central);
        var fila = await cn.QueryFirstOrDefaultAsync<(Guid RecuperoId, Guid UsuarioId, Guid? EmpresaId, string NombreUsuario, string Codigo)>(
            @"SELECT r.Id, u.Id, u.EmpresaId, u.NombreUsuario, e.Codigo
              FROM dbo.RecuperoClave r
              JOIN dbo.Usuario u ON u.Id = r.UsuarioId
              LEFT JOIN dbo.Empresa e ON e.Id = u.EmpresaId
              WHERE r.TokenHash = @hash AND r.Usado = 0 AND r.Vence > GETDATE() AND u.Activo = 1",
            new { hash = Hash(token) });
        if (fila.RecuperoId == Guid.Empty) return ("El link venció o ya se usó. Pedí uno nuevo.", null);

        var err = await _usuarios.CambiarClaveAsync(fila.UsuarioId, fila.EmpresaId, claveNueva);
        if (err != null) return (err, null);

        // Invalida este link y cualquier otro pendiente del mismo usuario
        await cn.ExecuteAsync("UPDATE dbo.RecuperoClave SET Usado = 1 WHERE UsuarioId = @u AND Usado = 0", new { u = fila.UsuarioId });
        var codigo = fila.Codigo ?? RolUsuario.CodigoCmTech;
        await Log(codigo, fila.NombreUsuario, fila.UsuarioId, true, "Recupero: clave restablecida", ip);
        return (null, codigo);
    }

    private Task Log(string empresa, string usuario, Guid? id, bool exito, string motivo, string ip) =>
        _usuarios.RegistrarEventoAsync(empresa, usuario, id, exito, motivo, ip);

    private static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string Corta(string s, int max) => s == null ? null : (s.Length > max ? s[..max] : s);
}

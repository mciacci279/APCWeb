using System.Security.Claims;
using APC.Core.Tenants;
using Microsoft.AspNetCore.Components.Authorization;

namespace APC.Web.Sesion;

public class SesionActual
{
    public Guid UsuarioId { get; init; }
    public string Usuario { get; init; }
    public string Nombre { get; init; }
    /// <summary>Id de la empresa en la base central (Guid.Empty para CM Tech).</summary>
    public Guid EmpresaId { get; init; }
    public bool EsAdmin { get; init; }
    public bool EsSuperAdmin { get; init; }
    /// <summary>Empresa del usuario; null para los usuarios de CM Tech.</summary>
    public TenantConfig Tenant { get; init; }
    public string Cs => Tenant?.ConnectionString;
}

/// <summary>Datos del usuario logueado y de su empresa, leídos de la cookie.</summary>
public class SesionService
{
    public const string RolSuperAdmin = "CMTECH";
    public const string RolAdmin = "APC_ADMIN";
    public const string RolEmpleado = "APC_EMPLEADO";

    private readonly AuthenticationStateProvider _auth;
    private readonly TenantRegistry _tenants;
    private SesionActual _cache;

    public SesionService(AuthenticationStateProvider auth, TenantRegistry tenants)
    {
        _auth = auth;
        _tenants = tenants;
    }

    /// <summary>
    /// Devuelve null si no hay sesión o si la empresa del usuario fue suspendida o eliminada.
    /// </summary>
    public async Task<SesionActual> ObtenerAsync()
    {
        if (_cache != null) return _cache;
        var user = (await _auth.GetAuthenticationStateAsync()).User;
        if (user?.Identity?.IsAuthenticated != true) return null;

        var id = Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var g) ? g : Guid.Empty;
        if (user.IsInRole(RolSuperAdmin))
        {
            _cache = new SesionActual
            {
                UsuarioId = id, Usuario = user.Identity.Name, Nombre = user.FindFirst("nombre")?.Value,
                EsSuperAdmin = true
            };
            return _cache;
        }

        var tenant = _tenants.Buscar(user.FindFirst("tenant")?.Value);
        if (tenant == null) return null;

        _cache = new SesionActual
        {
            UsuarioId = id,
            Usuario = user.Identity.Name,
            Nombre = user.FindFirst("nombre")?.Value,
            EmpresaId = tenant.Id,
            EsAdmin = user.IsInRole(RolAdmin),
            Tenant = tenant
        };
        return _cache;
    }
}

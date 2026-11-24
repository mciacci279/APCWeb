using System.Security.Claims;
using APC.Core.Modelos;
using APC.Core.Servicios;
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
    /// <summary>Supervisor de sucursal: ve las cajas de su sucursal.</summary>
    public bool EsSupervisor { get; init; }
    public bool EsSuperAdmin { get; init; }
    /// <summary>Empresa del usuario; null para los usuarios de CM Tech.</summary>
    public TenantConfig Tenant { get; init; }
    public string Cs => Tenant?.ConnectionString;

    /// <summary>Sucursal en la que trabaja el usuario. Null solo para los usuarios de CM Tech.</summary>
    public Sucursal Sucursal { get; set; }

    /// <summary>Punto de venta con el que factura: el de su sucursal.</summary>
    public int PuntoVenta => Sucursal?.PuntoVenta ?? Tenant?.PuntoVenta ?? 0;

    /// <summary>
    /// Módulo habilitado para este usuario: primero lo que tiene la empresa y después lo que
    /// CM Tech le dejó a su rol. CM Tech ve todo.
    /// </summary>
    public bool TieneModulo(string clave)
    {
        if (EsSuperAdmin || Tenant == null) return true;
        if (!Tenant.TieneModulo(clave)) return false;
        if (EsAdmin) return true;
        var delRol = EsSupervisor ? Tenant.ModulosSupervisor : Tenant.ModulosEmpleado;
        return Modulo.EnLista(delRol, clave, Modulo.PorDefectoRol);
    }
    public Guid SucursalId => Sucursal?.Id ?? Guid.Empty;

    /// <summary>
    /// Caja en la que trabaja el usuario, si la sucursal tiene varias (ver PuestoCajaService).
    /// Null si la sucursal tiene una sola o ninguna, o si el administrador entró sin caja.
    /// </summary>
    public PuestoCajaSesion Caja { get; set; }

    /// <summary>El administrador eligió entrar sin tomar una caja (solo para consultar).</summary>
    public bool SinCaja { get; set; }
}

/// <summary>Datos del usuario logueado y de su empresa, leídos de la cookie.</summary>
public class SesionService
{
    public const string RolSuperAdmin = "CMTECH";
    public const string RolAdmin = "APC_ADMIN";
    public const string RolSupervisor = "APC_SUPERVISOR";
    public const string RolEmpleado = "APC_EMPLEADO";

    private readonly AuthenticationStateProvider _auth;
    private readonly TenantRegistry _tenants;
    private readonly UsuarioService _usuarios;
    private readonly SucursalService _sucursales;
    private SesionActual _cache;

    public SesionService(AuthenticationStateProvider auth, TenantRegistry tenants,
        UsuarioService usuarios, SucursalService sucursales)
    {
        _auth = auth;
        _tenants = tenants;
        _usuarios = usuarios;
        _sucursales = sucursales;
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
            EsSupervisor = user.IsInRole(RolSupervisor),
            Tenant = tenant
        };
        _cache.Sucursal = await ResolverSucursalAsync(tenant, id);
        return _cache;
    }

    /// <summary>
    /// La sucursal asignada al usuario. Si no tiene (o la dieron de baja) usa la primera activa,
    /// y si la empresa todavía no tiene ninguna, se crea «Casa central» con su punto de venta.
    /// </summary>
    private async Task<Sucursal> ResolverSucursalAsync(TenantConfig tenant, Guid usuarioId)
    {
        try
        {
            var asignada = await _usuarios.SucursalDeAsync(usuarioId);
            if (asignada != null)
            {
                var s = await _sucursales.ObtenerAsync(tenant.ConnectionString, asignada.Value);
                if (s is { Activa: true }) return s;
            }

            var activas = await _sucursales.ListarAsync(tenant.ConnectionString, incluirInactivas: false);
            if (activas.Count > 0) return activas[0];

            return await _sucursales.AsegurarCasaCentralAsync(tenant);
        }
        catch (Exception)
        {
            // Si todavía no se corrieron los scripts de sucursales, se sigue con el punto de venta de la empresa.
            return null;
        }
    }

    /// <summary>Vuelve a leer la sucursal (después de cambiarla desde el panel).</summary>
    public void Olvidar() => _cache = null;
}

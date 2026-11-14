using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace APC.Core.Tenants;

/// <summary>
/// Empresas cargadas desde la base central, con caché en memoria de 2 minutos.
/// El panel de CM Tech llama a Invalidar() después de cada cambio.
/// </summary>
public class TenantRegistry
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(2);
    private readonly IOptionsMonitor<TenantOptions> _opt;
    private readonly object _lock = new();
    private Dictionary<string, TenantConfig> _cache;
    private DateTime _cargado;

    public TenantRegistry(IOptionsMonitor<TenantOptions> opt) => _opt = opt;

    public string CarpetaDatos => _opt.CurrentValue.CarpetaDatos;
    public string CarpetaCertificados => _opt.CurrentValue.CarpetaCertificados;
    public string UrlPublica => _opt.CurrentValue.UrlPublica;
    public string Central => _opt.CurrentValue.CentralConnectionString;

    /// <summary>Empresa activa con ese código, o null.</summary>
    public TenantConfig Buscar(string codigo)
    {
        var t = BuscarCualquiera(codigo);
        return t != null && t.Activa ? t : null;
    }

    /// <summary>Empresa con ese código aunque esté suspendida, o null.</summary>
    public TenantConfig BuscarCualquiera(string codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo)) return null;
        return Cache().TryGetValue(codigo.Trim(), out var t) ? t : null;
    }

    public void Invalidar()
    {
        lock (_lock) _cache = null;
    }

    private Dictionary<string, TenantConfig> Cache()
    {
        lock (_lock)
        {
            if (_cache != null && DateTime.UtcNow - _cargado < Vigencia) return _cache;

            using var cn = new SqlConnection(Central);
            cn.Open();
            var lista = cn.Query<TenantConfig>(
                @"SELECT Id, Codigo, Nombre, NombreFantasia, ConnectionString, PuntoVenta, Ambiente, CertificadoPath, ClavePath,
                         Concepto, Moneda, Activa, MotivoSuspension, FechaAlta, LeyendaTicket
                  FROM dbo.Empresa");
            _cache = lista.ToDictionary(t => t.Codigo.Trim(), StringComparer.OrdinalIgnoreCase);
            _cargado = DateTime.UtcNow;
            return _cache;
        }
    }
}

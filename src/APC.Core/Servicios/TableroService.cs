using APC.Core.Datos;
using APC.Core.Tenants;
using Dapper;
using Microsoft.Data.SqlClient;

namespace APC.Core.Servicios;

/// <summary>Uso de una empresa, para el tablero de CM Tech.</summary>
public class UsoEmpresa
{
    public TenantConfig Empresa { get; set; }
    public int UsuariosActivos { get; set; }
    public DateTime? UltimoIngreso { get; set; }

    public int FacturasMes { get; set; }
    public int FacturasMesAnterior { get; set; }
    public decimal FacturadoMes { get; set; }
    public DateTime? UltimaFactura { get; set; }
    /// <summary>Si no se pudo leer la base de la empresa, el motivo.</summary>
    public string Error { get; set; }

    /// <summary>Sin ingresos ni facturas en los últimos 30 días.</summary>
    public bool SinUso =>
        (UltimoIngreso == null || UltimoIngreso < DateTime.Now.AddDays(-30)) &&
        (UltimaFactura == null || UltimaFactura < DateTime.Now.AddDays(-30));
}

/// <summary>
/// Tablero de CM Tech: cuánto usa el sistema cada empresa y cómo está su abono.
/// Lee la base central y, una por una, la base de cada empresa.
/// </summary>
public class TableroService
{
    private readonly EmpresaCentralService _empresas;
    private readonly TenantRegistry _tenants;

    public TableroService(EmpresaCentralService empresas, TenantRegistry tenants)
    {
        _empresas = empresas;
        _tenants = tenants;
    }

    public async Task<List<UsoEmpresa>> UsoAsync()
    {
        var empresas = await _empresas.ListarAsync();

        Dictionary<Guid, (int Usuarios, DateTime? Ultimo)> porEmpresa;
        using (var cn = await Db.AbrirAsync(_tenants.Central))
        {
            porEmpresa = (await cn.QueryAsync<(Guid EmpresaId, int Usuarios, DateTime? Ultimo)>(
                @"SELECT EmpresaId, SUM(CASE WHEN Activo = 1 THEN 1 ELSE 0 END), MAX(UltimoIngreso)
                  FROM dbo.Usuario WHERE EmpresaId IS NOT NULL GROUP BY EmpresaId"))
                .ToDictionary(x => x.EmpresaId, x => (x.Usuarios, x.Ultimo));
        }

        var tareas = empresas.Select(async e =>
        {
            var uso = new UsoEmpresa { Empresa = e };
            if (porEmpresa.TryGetValue(e.Id, out var u)) { uso.UsuariosActivos = u.Usuarios; uso.UltimoIngreso = u.Ultimo; }
            await LeerFacturacionAsync(uso);
            return uso;
        });
        return (await Task.WhenAll(tareas)).ToList();
    }

    private static async Task LeerFacturacionAsync(UsoEmpresa uso)
    {
        try
        {
            // Si una base no responde, que no frene todo el tablero.
            var csb = new SqlConnectionStringBuilder(uso.Empresa.ConnectionString) { ConnectTimeout = 8 };
            using var cn = new SqlConnection(csb.ConnectionString);
            await cn.OpenAsync();

            var hoy = DateTime.Today;
            var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);
            var r = await cn.QueryFirstAsync<(int Mes, int Anterior, decimal Facturado, DateTime? Ultima)>(
                @"SELECT
                    (SELECT COUNT(*) FROM dbo.Factura WHERE Fecha >= @inicioMes AND CAE IS NOT NULL),
                    (SELECT COUNT(*) FROM dbo.Factura WHERE Fecha >= @inicioAnterior AND Fecha < @inicioMes AND CAE IS NOT NULL),
                    (SELECT ISNULL(SUM(MontoTotal), 0) FROM dbo.Factura WHERE Fecha >= @inicioMes AND CAE IS NOT NULL),
                    (SELECT MAX(Fecha) FROM dbo.Factura)",
                new { inicioMes, inicioAnterior = inicioMes.AddMonths(-1) }, commandTimeout: 15);

            uso.FacturasMes = r.Mes;
            uso.FacturasMesAnterior = r.Anterior;
            uso.FacturadoMes = r.Facturado;
            uso.UltimaFactura = r.Ultima;
        }
        catch (Exception ex)
        {
            uso.Error = ex.GetBaseException().Message;
        }
    }
}

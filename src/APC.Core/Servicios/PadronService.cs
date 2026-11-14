using APC.Arca;
using APC.Core.Tenants;

namespace APC.Core.Servicios;

/// <summary>
/// Trae los datos de un CUIT desde el padrón de ARCA, para no cargarlos a mano.
/// Usa el mismo certificado de la empresa que se usa para facturar.
/// </summary>
public class PadronService
{
    private readonly CatalogoService _catalogo;
    private readonly TenantRegistry _tenants;

    public PadronService(CatalogoService catalogo, TenantRegistry tenants)
    {
        _catalogo = catalogo;
        _tenants = tenants;
    }

    public async Task<PersonaPadron> ConsultarAsync(TenantConfig tenant, string cuit)
    {
        var limpio = new string((cuit ?? "").Where(char.IsDigit).ToArray());
        if (limpio.Length != 11)
            return new PersonaPadron { Error = "El CUIT tiene que tener 11 dígitos." };

        if (string.IsNullOrWhiteSpace(tenant.CertificadoPath) || string.IsNullOrWhiteSpace(tenant.ClavePath))
            return new PersonaPadron { Error = "La empresa no tiene cargado el certificado de ARCA." };

        var empresa = await _catalogo.EmpresaAsync(tenant.ConnectionString);
        if (empresa == null || empresa.CuitNumero <= 0)
            return new PersonaPadron { Error = "Faltan los datos fiscales de la empresa." };

        var cache = Path.Combine(_tenants.CarpetaDatos, "ta", empresa.CuitNumero + "_" + tenant.Ambiente);

        return await PadronA13Client.ConsultarAsync(
            limpio, empresa.CuitNumero, tenant.CertificadoPath, tenant.ClavePath,
            tenant.UrlWsaa, tenant.UrlPadron, cache);
    }
}

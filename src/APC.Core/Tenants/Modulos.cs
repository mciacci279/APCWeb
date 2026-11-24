using System.Linq;

namespace APC.Core.Tenants;

/// <summary>
/// Módulos que CM Tech habilita o deshabilita por empresa (columna Empresa.Modulos, script 17).
/// Se guardan como una lista separada por comas, ej. "FACT,CAJA,COMP".
/// Vacío o NULL = la empresa ve todo, que es como venía funcionando.
/// </summary>
public static class Modulo
{
    public const string Facturar = "FACT";
    public const string Caja = "CAJA";
    public const string Presupuestos = "PRESU";
    public const string Productos = "PROD";
    public const string Clientes = "CLI";
    public const string Sucursales = "SUC";
    public const string Comprobantes = "COMP";
    public const string Reportes = "REP";
    public const string Usuarios = "USU";
    public const string Precios = "PREC";

    /// <summary>Los módulos en el orden en que aparecen en el menú.</summary>
    public static readonly (string Clave, string Titulo, string Detalle)[] Todos =
    {
        (Facturar, "Facturar", "Emitir facturas y notas de pedido"),
        (Caja, "Arqueo de caja", "Apertura, cierre y arqueo del turno"),
        (Presupuestos, "Presupuestos", "Presupuestos y su listado"),
        (Productos, "Productos", "Alta de productos, precios y stock"),
        (Clientes, "Clientes", "Alta de clientes y padrón de ARCA"),
        (Sucursales, "Sucursales", "Ver las sucursales y mover stock"),
        (Comprobantes, "Comprobantes", "Listado, reimpresión y anulación"),
        (Reportes, "Reportes", "Ventas, Libro IVA y rankings"),
        (Precios, "Precios y formas de pago", "Tipos de precio y ajuste por forma de pago"),
        (Usuarios, "Usuarios", "Alta de usuarios del negocio")
    };

    /// <summary>
    /// Lo que ve un supervisor o un cajero mientras CM Tech no configure otra cosa:
    /// el trabajo diario, que es lo que veían antes de que existieran los módulos.
    /// </summary>
    public static readonly string[] PorDefectoRol = { Facturar, Caja, Presupuestos };

    /// <summary>Si la clave está en una lista separada por comas. Lista vacía = se usa la de por defecto.</summary>
    public static bool EnLista(string lista, string clave, string[] siEstaVacia = null)
    {
        if (string.IsNullOrWhiteSpace(clave)) return true;
        if (string.IsNullOrWhiteSpace(lista))
            return siEstaVacia == null || siEstaVacia.Contains(clave, StringComparer.OrdinalIgnoreCase);
        foreach (var m in lista.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(m.Trim(), clave, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Ruta de cada módulo, para saber qué página corresponde a cuál.</summary>
    public static string DeRuta(string ruta)
    {
        ruta = "/" + (ruta ?? "").Trim('/').ToLowerInvariant();
        if (ruta == "/") return Facturar;
        if (ruta.StartsWith("/arqueo")) return Caja;
        if (ruta.StartsWith("/cajas")) return Caja;
        if (ruta.StartsWith("/presupuesto")) return Presupuestos;
        if (ruta.StartsWith("/productos")) return Productos;
        if (ruta.StartsWith("/clientes")) return Clientes;
        if (ruta.StartsWith("/sucursales")) return Sucursales;
        if (ruta.StartsWith("/comprobantes")) return Comprobantes;
        if (ruta.StartsWith("/reportes")) return Reportes;
        if (ruta.StartsWith("/usuarios")) return Usuarios;
        if (ruta.StartsWith("/precios")) return Precios;
        return null;
    }
}

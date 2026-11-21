using System.Globalization;
using APC.Core.Datos;
using APC.Core.Modelos;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Lee los códigos de barra que imprime la balanza Systel y los convierte en un renglón.
/// El formato es el mismo que entiende el sistema de escritorio:
///
///   20 PPPP CCCCCC X   venta por peso   (PPPP = PLU, CCCCCC = gramos)
///   21 PPPP CCCCCC X   venta por unidad (CCCCCC = unidades)
///   22 AA IIIIIIII X   total del ticket (AA = artículos, IIIIIIII = importe)
///
/// En modo cantidad el precio sale del producto; en modo importe manda el de la balanza.
/// </summary>
public class BalanzaService
{
    private readonly CatalogoService _catalogo;
    public BalanzaService(CatalogoService catalogo) => _catalogo = catalogo;

    // ------------------------------------------------------------------ configuración

    public async Task<ConfigBalanza> ConfigAsync(string cs)
    {
        try
        {
            using var cn = await Db.AbrirAsync(cs);
            var existe = await cn.ExecuteScalarAsync<int>(
                "SELECT CASE WHEN OBJECT_ID(N'dbo.ApcWebBalanza', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
            if (!existe) return new ConfigBalanza();

            return await cn.QueryFirstOrDefaultAsync<ConfigBalanza>(
                       "SELECT TOP 1 Activa, Modo, Decimales, CodigoSuma FROM dbo.ApcWebBalanza")
                   ?? new ConfigBalanza();
        }
        catch { return new ConfigBalanza(); }
    }

    public async Task<string> GuardarAsync(string cs, ConfigBalanza c)
    {
        c.CodigoSuma = string.IsNullOrWhiteSpace(c.CodigoSuma) ? null : c.CodigoSuma.Trim();
        if (c.Activa && c.CodigoSuma == null)
            return "Cargá el código del producto que recibe el total del ticket.";

        using var cn = await Db.AbrirAsync(cs);
        var existe = await cn.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN OBJECT_ID(N'dbo.ApcWebBalanza', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
        if (!existe) return "Falta correr el script 25 en la base de esta empresa.";

        await cn.ExecuteAsync(
            @"UPDATE dbo.ApcWebBalanza SET Activa = @Activa, Modo = @Modo,
                     Decimales = @Decimales, CodigoSuma = @CodigoSuma;
              IF @@ROWCOUNT = 0
                  INSERT INTO dbo.ApcWebBalanza (Unica, Activa, Modo, Decimales, CodigoSuma)
                  VALUES (1, @Activa, @Modo, @Decimales, @CodigoSuma);",
            new { c.Activa, Modo = c.PorCantidad ? "Q" : "P", c.Decimales, c.CodigoSuma });
        return null;
    }

    // ------------------------------------------------------------------ lectura del código

    /// <summary>Desarma el código. Si no es de balanza, devuelve EsDeBalanza = false.</summary>
    public static LecturaBalanza Leer(string codigo, ConfigBalanza cfg)
    {
        var vacio = new LecturaBalanza();
        if (cfg == null || !cfg.Activa) return vacio;

        codigo = (codigo ?? "").Trim();
        if (codigo.Length != 13 || !codigo.All(char.IsDigit)) return vacio;

        var tipo = codigo[..2];
        if (tipo != TipoCodigoBalanza.Peso && tipo != TipoCodigoBalanza.Unidad && tipo != TipoCodigoBalanza.Total)
            return vacio;

        var l = new LecturaBalanza { EsDeBalanza = true, Tipo = tipo };

        if (tipo == TipoCodigoBalanza.Total)
        {
            // 22 AA IIIIIIII X: los dos dígitos de la cantidad de artículos no se usan,
            // el renglón entra como uno solo con el total del ticket.
            l.Cantidad = 1;
            l.Precio = cfg.Decimales
                ? Numero(codigo.Substring(4, 6) + "." + codigo.Substring(10, 2))
                : Numero(codigo.Substring(4, 8));
            return l;
        }

        l.Plu = codigo.Substring(2, 4).TrimStart('0');
        if (l.Plu.Length == 0) l.Plu = "0";

        if (cfg.PorCantidad)
        {
            var campo = Numero(codigo.Substring(6, 6));
            // Por peso el campo viene en gramos; por unidad, en unidades enteras.
            l.Cantidad = tipo == TipoCodigoBalanza.Peso
                ? Math.Round(campo / 1000m, 3, MidpointRounding.AwayFromZero)
                : campo;
        }
        else
        {
            l.Cantidad = 1;
            l.Precio = cfg.Decimales
                ? Numero(codigo.Substring(6, 4) + "." + codigo.Substring(10, 2))
                : Numero(codigo.Substring(6, 6));
        }
        return l;
    }

    private static decimal Numero(string digitos) =>
        decimal.TryParse(digitos, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    // ------------------------------------------------------------------ resolver contra el catálogo

    /// <summary>
    /// Busca el producto del código y devuelve el renglón listo para cargar.
    /// Null si el código no era de balanza: ahí sigue la búsqueda de siempre.
    /// </summary>
    public async Task<RenglonBalanza> ResolverAsync(string cs, string codigo, ConfigBalanza cfg)
    {
        var l = Leer(codigo, cfg);
        if (!l.EsDeBalanza) return null;

        var buscado = l.EsTotal ? cfg.CodigoSuma : l.Plu;
        var producto = await _catalogo.ProductoPorCodigoAsync(cs, buscado);

        if (producto == null)
            return new RenglonBalanza
            {
                EsTotal = l.EsTotal,
                Error = l.EsTotal
                    ? $"No hay ningún producto con el código «{buscado}», que es el que recibe el total del ticket de balanza."
                    : $"La balanza mandó el PLU {buscado} y no hay ningún producto con ese código."
            };

        if (l.Cantidad <= 0)
            return new RenglonBalanza { EsTotal = l.EsTotal, Error = "El código de la balanza vino con cantidad cero." };

        return new RenglonBalanza
        {
            Producto = producto,
            Cantidad = l.Cantidad,
            Precio = l.Precio,
            EsTotal = l.EsTotal
        };
    }
}

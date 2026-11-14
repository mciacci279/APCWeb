using APC.Core.Datos;
using APC.Core.Modelos;
using Dapper;

namespace APC.Core.Servicios;

public class CatalogoService
{
    private const string ColumnasProducto =
        @"Id, Codigo, Nombre, PrecioVenta1, Stock,
          ISNULL(IvaDiferenciado, 0) AS IvaDiferenciado, ISNULL(IvaExento, 0) AS IvaExento,
          ISNULL(OmitirControlStock, 0) AS OmitirControlStock";

    /// <summary>Busca primero por código exacto (lector de barras); si no hay, por descripción.</summary>
    public async Task<List<ProductoItem>> BuscarProductosAsync(string cs, string texto, int max = 15)
    {
        if (string.IsNullOrWhiteSpace(texto)) return new();
        texto = texto.Trim();
        using var cn = await Db.AbrirAsync(cs);

        var exacto = (await cn.QueryAsync<ProductoItem>(
            $"SELECT TOP 1 {ColumnasProducto} FROM dbo.Producto WHERE Codigo = @t AND ISNULL(Activo, 1) = 1",
            new { t = texto })).ToList();
        if (exacto.Count > 0) return exacto;

        return (await cn.QueryAsync<ProductoItem>(
            $@"SELECT TOP (@max) {ColumnasProducto} FROM dbo.Producto
               WHERE ISNULL(Activo, 1) = 1 AND (Nombre LIKE @like OR Codigo LIKE @like)
               ORDER BY Nombre",
            new { like = "%" + texto.Replace("[", "[[]").Replace("%", "[%]") + "%", max })).ToList();
    }

    /// <summary>Productos por Id (incluye inactivos), para recuperar un presupuesto.</summary>
    public async Task<List<ProductoItem>> ProductosPorIdAsync(string cs, IEnumerable<Guid> ids)
    {
        var lista = ids.ToList();
        if (lista.Count == 0) return new();
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<ProductoItem>(
            $"SELECT {ColumnasProducto} FROM dbo.Producto WHERE Id IN @ids", new { ids = lista })).ToList();
    }

    private const string ColumnasFormaPago =
        @"Id, Nombre, ISNULL(PorcentajeRecargo, 0) AS PorcentajeRecargo,
          ISNULL(PorcentajeDescuento, 0) AS PorcentajeDescuento, ISNULL(EsCtaCte, 0) AS EsCtaCte";

    public async Task<List<FormaPagoItem>> FormasPagoAsync(string cs)
    {
        var lista = await LeerFormasPagoAsync(cs, null);
        return lista.OrderBy(f => f.Nombre).ToList();
    }

    public async Task<FormaPagoItem> FormaPagoAsync(string cs, Guid id) =>
        (await LeerFormasPagoAsync(cs, id)).FirstOrDefault();

    /// <summary>
    /// Lee las formas de pago y aplica la lista de precios como APC (cmbFormaPago_SelectedIndexChanged +
    /// SetRecargo): lista 2 o 3 = recargo PORC_PVTA2/3 sobre el precio de lista (descuento si es negativo).
    /// Si la forma de pago ya tiene un recargo o descuento propio, se usa ese.
    /// </summary>
    private async Task<List<FormaPagoItem>> LeerFormasPagoAsync(string cs, Guid? id)
    {
        List<FormaPagoItem> lista;
        using (var cn = await Db.AbrirAsync(cs))
        {
            var filtro = id.HasValue ? " WHERE Id = @id" : "";
            try
            {
                lista = (await cn.QueryAsync<FormaPagoItem>(
                    $"SELECT {ColumnasFormaPago}, ISNULL(Precio, 1) AS ListaPrecio FROM dbo.FormaPago{filtro}", new { id })).ToList();
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207)
            {
                // Base sin la columna Precio: todas usan la lista 1
                lista = (await cn.QueryAsync<FormaPagoItem>(
                    $"SELECT {ColumnasFormaPago} FROM dbo.FormaPago{filtro}", new { id })).ToList();
            }
        }

        if (lista.Any(f => f.ListaPrecio is 2 or 3))
        {
            var cfg = await ConfiguracionAsync(cs);
            decimal Porc(string c) => cfg.TryGetValue(c, out var v) && v.Numero.HasValue ? (decimal)v.Numero.Value : 0m;
            foreach (var f in lista.Where(f => f.ListaPrecio is 2 or 3 && f.PorcentajeRecargo == 0 && f.PorcentajeDescuento == 0))
            {
                var p = Porc(f.ListaPrecio == 2 ? "PORC_PVTA2" : "PORC_PVTA3");
                if (p > 0) f.PorcentajeRecargo = p;
                else if (p < 0) f.PorcentajeDescuento = -p;
            }
        }
        return lista;
    }

    public async Task<EmpresaDatos> EmpresaAsync(string cs)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.QueryFirstOrDefaultAsync<EmpresaDatos>(
            @"SELECT TOP 1 e.Id, e.Nombre, e.Descripcion, e.Cuit, e.IngresosBrutos, e.FechaInicioActividades,
                     e.Domicilio, e.Telefono1, e.CP, e.Email, i.Nombre AS CondicionIva,
                     ISNULL(i.EsMonotributo, 0) AS EsMonotributo
              FROM dbo.Empresa e LEFT JOIN dbo.Iva i ON i.Id = e.IvaId");
    }

    public async Task<Dictionary<string, (string Texto, bool? Bit, double? Numero)>> ConfiguracionAsync(string cs)
    {
        using var cn = await Db.AbrirAsync(cs);
        var filas = await cn.QueryAsync<(string Codigo, string ValorString, bool? ValorBit, double? ValorFloat)>(
            "SELECT Codigo, ValorString, ValorBit, ValorFloat FROM dbo.Configuracion");
        var d = new Dictionary<string, (string, bool?, double?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in filas) d[f.Codigo.Trim()] = (f.ValorString, f.ValorBit, f.ValorFloat);
        return d;
    }
}

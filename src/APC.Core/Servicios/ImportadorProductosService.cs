using System.Data;
using System.Globalization;
using System.Text;
using APC.Core.Datos;
using Dapper;
using ExcelDataReader;
using Microsoft.Data.SqlClient;

namespace APC.Core.Servicios;

public class FilaImportacion
{
    public string Codigo { get; set; }
    public string Descripcion { get; set; }
    public decimal Cantidad { get; set; }
    public decimal PrecioCosto { get; set; }
    public decimal PrecioVenta1 { get; set; }
    public decimal PrecioVenta2 { get; set; }
    public decimal PrecioVenta3 { get; set; }
    public string Familia { get; set; }
    public bool IvaDiferenciado { get; set; }
    public bool Activo { get; set; } = true;
    /// <summary>Solo en la vista previa: si el código ya existe en la base.</summary>
    public bool Existe { get; set; }
}

public class LecturaImportacion
{
    public List<FilaImportacion> Filas { get; set; } = new();
    public int FilasLeidas { get; set; }
    public int FilasSinCodigo { get; set; }
    public int CodigosRepetidos { get; set; }
    public List<string> Avisos { get; set; } = new();
    public string Error { get; set; }
    // Vista previa contra la base
    public int Nuevos { get; set; }
    public int Existentes { get; set; }
    public List<string> RubrosNuevos { get; set; } = new();
}

public class ResultadoImportacion
{
    public bool Ok { get; set; }
    public string Error { get; set; }
    public int Nuevos { get; set; }
    public int Actualizados { get; set; }
    public int RubrosCreados { get; set; }
}

/// <summary>
/// Importación masiva de productos desde Excel, con las mismas columnas y reglas que el importador de
/// APC.WinForms (frmProductoImportar): CODIGOBARRA, DESCRIPCION, CANTIDAD, PRECIOCOSTO, PRECIOVENTA1..3,
/// FAMILIA, IVADIFERENCIADO, ACTIVO.
/// </summary>
public class ImportadorProductosService
{
    public const long TamanioMaximo = 15 * 1024 * 1024;
    public static readonly string[] Columnas =
        { "CODIGOBARRA", "DESCRIPCION", "CANTIDAD", "PRECIOCOSTO", "PRECIOVENTA1", "PRECIOVENTA2", "PRECIOVENTA3", "FAMILIA", "IVADIFERENCIADO", "ACTIVO" };

    static ImportadorProductosService()
    {
        // Necesario para leer .xls (formato binario viejo) en .NET
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    // ------------------------------------------------------------------ lectura

    /// <summary>Lee el Excel (.xls o .xlsx). La fila 1 tiene los nombres de columna; se usa la primera hoja.</summary>
    public LecturaImportacion Leer(Stream contenido)
    {
        var r = new LecturaImportacion();
        DataTable tabla;
        try
        {
            using var reader = ExcelReaderFactory.CreateReader(contenido);
            var ds = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = true }
            });
            if (ds.Tables.Count == 0) { r.Error = "El archivo no tiene hojas."; return r; }
            tabla = ds.Tables[0];
        }
        catch (Exception ex)
        {
            r.Error = "No se pudo leer el archivo. Verificá que sea un Excel (.xls o .xlsx). Detalle: " + ex.Message;
            return r;
        }

        var cols = tabla.Columns.Cast<DataColumn>()
            .ToDictionary(c => Normalizar(c.ColumnName), c => c.Ordinal);
        if (!cols.ContainsKey("CODIGOBARRA") && !cols.ContainsKey("CODIGO"))
        {
            r.Error = "No se encontró la columna CODIGOBARRA en la primera hoja. Usá la plantilla (fila 1 con los títulos).";
            return r;
        }
        foreach (var c in Columnas.Where(c => !cols.ContainsKey(c) && c != "CODIGOBARRA"))
            r.Avisos.Add($"Falta la columna {c}: se toma vacía.");

        object Col(DataRow fila, string nombre, string alternativa = null) =>
            cols.TryGetValue(nombre, out var i) || (alternativa != null && cols.TryGetValue(alternativa, out i)) ? fila[i] : null;

        var leidas = new List<FilaImportacion>();
        foreach (DataRow fila in tabla.Rows)
        {
            r.FilasLeidas++;
            var codigo = Texto(Col(fila, "CODIGOBARRA", "CODIGO")).Replace("'", "");
            if (codigo.Length == 0)
            {
                if (fila.ItemArray.Any(v => v != null && v != DBNull.Value && v.ToString().Trim().Length > 0)) r.FilasSinCodigo++;
                else r.FilasLeidas--; // fila totalmente vacía: no cuenta
                continue;
            }
            leidas.Add(new FilaImportacion
            {
                Codigo = Recortar(codigo, 100),
                Descripcion = Recortar(Texto(Col(fila, "DESCRIPCION")), 200),
                Cantidad = Decimal(Col(fila, "CANTIDAD")),
                PrecioCosto = Decimal(Col(fila, "PRECIOCOSTO")),
                PrecioVenta1 = Decimal(Col(fila, "PRECIOVENTA1")),
                PrecioVenta2 = Decimal(Col(fila, "PRECIOVENTA2")),
                PrecioVenta3 = Decimal(Col(fila, "PRECIOVENTA3")),
                Familia = Recortar(Texto(Col(fila, "FAMILIA")), 50),
                IvaDiferenciado = Booleano(Col(fila, "IVADIFERENCIADO"), false),
                Activo = Booleano(Col(fila, "ACTIVO"), true)
            });
        }

        // Igual que APC: código repetido → se suman cantidades y se toma el último precio no cero
        r.Filas = leidas.GroupBy(x => x.Codigo).Select(g => new FilaImportacion
        {
            Codigo = g.Key,
            Descripcion = g.Last().Descripcion,
            Cantidad = g.Sum(x => x.Cantidad),
            PrecioCosto = UltimoNoCero(g.Select(x => x.PrecioCosto)),
            PrecioVenta1 = UltimoNoCero(g.Select(x => x.PrecioVenta1)),
            PrecioVenta2 = UltimoNoCero(g.Select(x => x.PrecioVenta2)),
            PrecioVenta3 = UltimoNoCero(g.Select(x => x.PrecioVenta3)),
            Familia = g.Last().Familia,
            IvaDiferenciado = g.Last().IvaDiferenciado,
            Activo = g.Last().Activo
        }).ToList();
        r.CodigosRepetidos = leidas.Count - r.Filas.Count;
        if (r.Filas.Count == 0 && r.Error == null) r.Error = "El archivo no tiene productos con código.";
        return r;
    }

    /// <summary>Marca qué códigos ya existen y qué rubros se van a crear (sin modificar nada).</summary>
    public async Task AnalizarAsync(string cs, LecturaImportacion l)
    {
        using var cn = await Db.AbrirAsync(cs);
        var codigos = new HashSet<string>(
            await cn.QueryAsync<string>("SELECT LTRIM(RTRIM(Codigo)) FROM dbo.Producto WHERE Codigo IS NOT NULL"),
            StringComparer.OrdinalIgnoreCase);
        var rubros = new HashSet<string>(
            await cn.QueryAsync<string>("SELECT LTRIM(RTRIM(Nombre)) FROM dbo.Rubro"), StringComparer.OrdinalIgnoreCase);

        foreach (var f in l.Filas) f.Existe = codigos.Contains(f.Codigo);
        l.Existentes = l.Filas.Count(f => f.Existe);
        l.Nuevos = l.Filas.Count - l.Existentes;
        l.RubrosNuevos = l.Filas.Select(f => f.Familia).Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !rubros.Contains(f)).OrderBy(f => f).ToList();
    }

    // ------------------------------------------------------------------ importación

    public async Task<ResultadoImportacion> ImportarAsync(string cs, Guid usuarioId, List<FilaImportacion> filas,
        bool sobreescribirDescripcion, bool reemplazarStock)
    {
        // La descripción admite 100 caracteres de fábrica y 200 con el script 30.
        var largoDescripcion = await ProductoService.LargoDescripcionAsync(cs);
        if (filas == null || filas.Count == 0) return new ResultadoImportacion { Error = "No hay filas para importar." };

        var dt = new DataTable();
        dt.Columns.Add("Codigo", typeof(string));
        dt.Columns.Add("Descripcion", typeof(string));
        dt.Columns.Add("Cantidad", typeof(decimal));
        dt.Columns.Add("PrecioCosto", typeof(decimal));
        dt.Columns.Add("PrecioVenta1", typeof(decimal));
        dt.Columns.Add("PrecioVenta2", typeof(decimal));
        dt.Columns.Add("PrecioVenta3", typeof(decimal));
        dt.Columns.Add("Activo", typeof(bool));
        dt.Columns.Add("IvaDiferenciado", typeof(bool));
        dt.Columns.Add("Familia", typeof(string));
        foreach (var f in filas)
            dt.Rows.Add(f.Codigo, Recortar(string.IsNullOrWhiteSpace(f.Descripcion) ? f.Codigo : f.Descripcion, largoDescripcion), f.Cantidad, f.PrecioCosto,
                f.PrecioVenta1, f.PrecioVenta2, f.PrecioVenta3, f.Activo, f.IvaDiferenciado,
                string.IsNullOrWhiteSpace(f.Familia) ? DBNull.Value : f.Familia);

        using var cn = (SqlConnection)await Db.AbrirAsync(cs);
        using var tx = cn.BeginTransaction();
        try
        {
            await cn.ExecuteAsync(
                @"CREATE TABLE #ImportProductos
                  (
                      Codigo          NVARCHAR(100)  NOT NULL PRIMARY KEY,
                      Descripcion     NVARCHAR(200)  NOT NULL,
                      Cantidad        DECIMAL(18, 3) NOT NULL,
                      PrecioCosto     DECIMAL(18, 4) NOT NULL,
                      PrecioVenta1    DECIMAL(18, 4) NOT NULL,
                      PrecioVenta2    DECIMAL(18, 4) NOT NULL,
                      PrecioVenta3    DECIMAL(18, 4) NOT NULL,
                      Activo          BIT            NOT NULL,
                      IvaDiferenciado BIT            NOT NULL,
                      Familia         NVARCHAR(50)   NULL
                  );", transaction: tx);

            using (var bulk = new SqlBulkCopy(cn, SqlBulkCopyOptions.Default, tx) { DestinationTableName = "#ImportProductos", BulkCopyTimeout = 300 })
            {
                foreach (DataColumn c in dt.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
                await bulk.WriteToServerAsync(dt);
            }

            // Mismas reglas que frmProductoImportar de APC.WinForms
            var res = await cn.QueryFirstAsync<(int Nuevos, int Actualizados, int Rubros)>(
                @"SET NOCOUNT ON;
                  DECLARE @nuevos INT, @actualizados INT, @rubros INT;

                  SELECT @nuevos = COUNT(*) FROM #ImportProductos s
                  WHERE NOT EXISTS (SELECT 1 FROM dbo.Producto p WHERE p.Codigo = s.Codigo);
                  SELECT @actualizados = COUNT(*) FROM #ImportProductos s
                  WHERE EXISTS (SELECT 1 FROM dbo.Producto p WHERE p.Codigo = s.Codigo);

                  -- (A) Rubros (FAMILIA) que todavía no existen
                  INSERT INTO dbo.Rubro (Id, Nombre, Observaciones)
                  SELECT NEWID(), f.Familia, N'Agregado automáticamente desde el importador de APC Web.'
                  FROM (SELECT DISTINCT LTRIM(RTRIM(Familia)) AS Familia FROM #ImportProductos
                        WHERE LTRIM(RTRIM(ISNULL(Familia, ''))) <> '') f
                  WHERE NOT EXISTS (SELECT 1 FROM dbo.Rubro r WHERE r.Nombre = f.Familia);
                  SET @rubros = @@ROWCOUNT;

                  -- (B) Productos nuevos
                  INSERT INTO dbo.Producto
                      (Id, Codigo, Nombre, Stock, StockMinimo, StockMaximo, PrecioCosto,
                       PrecioVenta1, PrecioVenta2, PrecioVenta3, Activo, IvaDiferenciado, RubroId)
                  SELECT NEWID(), s.Codigo, s.Descripcion, s.Cantidad, 0, 0, s.PrecioCosto,
                         s.PrecioVenta1, s.PrecioVenta2, s.PrecioVenta3, s.Activo, s.IvaDiferenciado, r.Id
                  FROM #ImportProductos s
                  OUTER APPLY (SELECT TOP 1 Id FROM dbo.Rubro WHERE Nombre = NULLIF(LTRIM(RTRIM(s.Familia)), '')) r
                  WHERE NOT EXISTS (SELECT 1 FROM dbo.Producto p WHERE p.Codigo = s.Codigo);

                  -- (C) Productos existentes: los precios en 0 no pisan los actuales
                  UPDATE p
                  SET p.Nombre          = CASE WHEN @sobreescribir = 1 THEN s.Descripcion ELSE p.Nombre END,
                      p.Stock           = CASE WHEN @pisarStock = 1 THEN s.Cantidad ELSE p.Stock + s.Cantidad END,
                      p.PrecioCosto     = CASE WHEN s.PrecioCosto  > 0 THEN s.PrecioCosto  ELSE p.PrecioCosto  END,
                      p.PrecioVenta1    = CASE WHEN s.PrecioVenta1 > 0 THEN s.PrecioVenta1 ELSE p.PrecioVenta1 END,
                      p.PrecioVenta2    = CASE WHEN s.PrecioVenta2 > 0 THEN s.PrecioVenta2 ELSE p.PrecioVenta2 END,
                      p.PrecioVenta3    = CASE WHEN s.PrecioVenta3 > 0 THEN s.PrecioVenta3 ELSE p.PrecioVenta3 END,
                      p.Activo          = s.Activo,
                      p.IvaDiferenciado = s.IvaDiferenciado,
                      p.RubroId         = CASE WHEN r.Id IS NOT NULL THEN r.Id ELSE p.RubroId END
                  FROM dbo.Producto p
                  INNER JOIN #ImportProductos s ON s.Codigo = p.Codigo
                  OUTER APPLY (SELECT TOP 1 Id FROM dbo.Rubro WHERE Nombre = NULLIF(LTRIM(RTRIM(s.Familia)), '')) r;

                  -- (D) Historial de stock y precios, con los valores que quedaron
                  INSERT INTO dbo.ProductoCambioStock
                      (Id, ProductoId, Cantidad, PU, PrecioVenta1, PrecioVenta2, PrecioVenta3, Fecha, Observaciones, UsuarioId)
                  SELECT NEWID(), p.Id, s.Cantidad, p.PrecioCosto, p.PrecioVenta1, p.PrecioVenta2, p.PrecioVenta3, GETDATE(),
                         CASE WHEN @pisarStock = 1 THEN N'[Importación Excel] Stock reemplazado' ELSE N'[Importación Excel]' END,
                         @usuarioId
                  FROM #ImportProductos s
                  INNER JOIN dbo.Producto p ON p.Codigo = s.Codigo;

                  DROP TABLE #ImportProductos;
                  SELECT @nuevos AS Nuevos, @actualizados AS Actualizados, @rubros AS Rubros;",
                new { sobreescribir = sobreescribirDescripcion, pisarStock = reemplazarStock, usuarioId },
                tx, commandTimeout: 300);

            tx.Commit();
            return new ResultadoImportacion { Ok = true, Nuevos = res.Nuevos, Actualizados = res.Actualizados, RubrosCreados = res.Rubros };
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            return new ResultadoImportacion { Error = "No se importó nada (se deshicieron todos los cambios). Detalle: " + ex.GetBaseException().Message };
        }
    }

    // ------------------------------------------------------------------ conversión de valores

    private static string Normalizar(string nombre) =>
        new string((nombre ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Texto(object v)
    {
        if (v == null || v is DBNull) return "";
        // Códigos de barra numéricos: sin notación científica ni ",0"
        if (v is double d) return d.ToString("0.###############", CultureInfo.InvariantCulture);
        return v.ToString().Trim();
    }

    private static decimal Decimal(object v)
    {
        if (v == null || v is DBNull) return 0m;
        if (v is double d) return (decimal)d;
        var s = v.ToString().Trim().Replace("$", "").Replace(" ", "");
        if (s.Length == 0) return 0m;
        if (s.Contains('.') && s.Contains(',')) s = s.Replace(".", "").Replace(",", "."); // 1.234,56
        else if (s.Contains(',')) s = s.Replace(",", ".");                                 // 1234,56
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0m;
    }

    private static bool Booleano(object v, bool porDefecto)
    {
        if (v == null || v is DBNull) return porDefecto;
        if (v is bool b) return b;
        if (v is double d) return d != 0;
        var s = v.ToString().Trim().ToLowerInvariant();
        if (s.Length == 0) return porDefecto;
        if (s is "1" or "true" or "verdadero" or "si" or "sí" or "s") return true;
        if (s is "0" or "false" or "falso" or "no" or "n") return false;
        return porDefecto;
    }

    private static decimal UltimoNoCero(IEnumerable<decimal> valores)
    {
        decimal ultimo = 0m;
        foreach (var v in valores) if (v != 0m) ultimo = v;
        return ultimo;
    }

    private static string Recortar(string s, int max) => s == null ? null : (s.Length > max ? s[..max] : s);
}

using System.Text.RegularExpressions;
using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Tenants;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Lleva la cuenta de qué script de la carpeta sql se corrió en cada base y los ejecuta.
/// Cada base anota lo suyo en su propia tabla dbo.ApcWebScript, así no hay una lista
/// central que se pueda desincronizar de la realidad.
/// </summary>
public class MigracionService
{
    private readonly TenantRegistry _tenants;
    private readonly string _carpeta;

    public MigracionService(TenantRegistry tenants, string carpeta)
    {
        _tenants = tenants;
        _carpeta = carpeta;
    }

    public string Carpeta => _carpeta;

    // ------------------------------------------------------------------ los scripts en disco

    private static readonly Regex NombreScript = new(@"^(\d{2,3})_(.+)\.sql$", RegexOptions.IgnoreCase);

    /// <summary>Scripts numerados de la carpeta sql, ordenados. Los sueltos (limpiar, ideas) no cuentan.</summary>
    public List<ScriptSql> Scripts()
    {
        var lista = new List<ScriptSql>();
        if (!Directory.Exists(_carpeta)) return lista;

        foreach (var ruta in Directory.GetFiles(_carpeta, "*.sql"))
        {
            var archivo = Path.GetFileName(ruta);
            var m = NombreScript.Match(archivo);
            if (!m.Success) continue;

            string cabecera;
            try { cabecera = Leer(ruta, 2000); }
            catch (IOException) { continue; }

            lista.Add(new ScriptSql
            {
                Nombre = archivo,
                Numero = int.Parse(m.Groups[1].Value),
                Ruta = ruta,
                Titulo = Marca(cabecera, "titulo") ?? m.Groups[2].Value.Replace('_', ' '),
                ParaCentral = EsCentral(Marca(cabecera, "destino"), archivo)
            });
        }
        return lista.OrderBy(x => x.Numero).ThenBy(x => x.Nombre).ToList();
    }

    /// <summary>Texto completo de un script, para previsualizarlo (hasta 512 KB).</summary>
    public string Contenido(ScriptSql script)
    {
        try
        {
            var info = new FileInfo(script.Ruta);
            if (!info.Exists) return null;
            return info.Length > 512 * 1024 ? Leer(script.Ruta, 512 * 1024) + "\n-- … (archivo recortado para la vista previa)" : File.ReadAllText(script.Ruta);
        }
        catch (IOException) { return null; }
    }

    private static bool EsCentral(string destino, string archivo) =>
        destino != null
            ? destino.Equals("central", StringComparison.OrdinalIgnoreCase)
            : archivo.Contains("_central", StringComparison.OrdinalIgnoreCase);

    private static string Marca(string texto, string clave)
    {
        var m = Regex.Match(texto, @"--\s*apcweb:" + clave + @"\s*=\s*(.+)");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string Leer(string ruta, int caracteres)
    {
        using var sr = new StreamReader(ruta);
        var buffer = new char[caracteres];
        var leidos = sr.Read(buffer, 0, caracteres);
        return new string(buffer, 0, leidos);
    }

    // ------------------------------------------------------------------ estado de una base

    /// <summary>Qué scripts tiene corridos una base y cuáles le faltan.</summary>
    public async Task<EstadoBase> EstadoAsync(string nombre, string cs, bool esCentral)
    {
        var estado = new EstadoBase { Nombre = nombre, ConnectionString = cs, EsCentral = esCentral };
        var scripts = Scripts().Where(x => x.ParaCentral == esCentral).ToList();

        try
        {
            using var cn = await Db.AbrirAsync(cs);
            estado.Base = cn.Database;
            await CrearRegistroAsync(cn);

            var corridos = (await cn.QueryAsync<ScriptCorrido>(
                "SELECT Nombre, Fecha, Quien, Resultado FROM dbo.ApcWebScript"))
                .ToDictionary(x => x.Nombre, StringComparer.OrdinalIgnoreCase);

            foreach (var s in scripts)
            {
                corridos.TryGetValue(s.Nombre, out var c);
                estado.Scripts.Add(new ScriptEnBase
                {
                    Script = s,
                    Fecha = c?.Fecha,
                    Quien = c?.Quien,
                    Resultado = c?.Resultado
                });
            }
            estado.Accesible = true;
        }
        catch (Exception ex)
        {
            estado.Error = ex.GetBaseException().Message;
            foreach (var s in scripts) estado.Scripts.Add(new ScriptEnBase { Script = s });
        }
        return estado;
    }

    /// <summary>Estado de la central, la plantilla y todas las empresas.</summary>
    public async Task<List<EstadoBase>> EstadoDeTodasAsync(List<TenantConfig> empresas)
    {
        var lista = new List<EstadoBase> { await EstadoAsync("Base central", _tenants.Central, true) };

        var plantilla = _tenants.BasePlantilla;
        if (!string.IsNullOrWhiteSpace(plantilla))
        {
            var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_tenants.Central)
            { InitialCatalog = plantilla };
            var e = await EstadoAsync("Plantilla (bases nuevas)", csb.ConnectionString, false);
            e.EsPlantilla = true;
            lista.Add(e);
        }

        foreach (var emp in empresas.OrderBy(x => x.Nombre))
        {
            var e = await EstadoAsync(emp.Nombre, emp.ConnectionString, false);
            e.EmpresaId = emp.Id;
            e.Codigo = emp.Codigo;
            e.Habilitada = emp.Habilitada;
            e.Estado = !emp.Activa ? "suspendida"
                     : emp.SuspendidaPorAbono ? "suspendida por abono"
                     : null;
            lista.Add(e);
        }
        return lista;
    }

    private static async Task CrearRegistroAsync(System.Data.IDbConnection cn) =>
        await cn.ExecuteAsync(
            @"IF OBJECT_ID(N'dbo.ApcWebScript', N'U') IS NULL
              CREATE TABLE dbo.ApcWebScript (
                  Nombre    NVARCHAR(150) NOT NULL PRIMARY KEY,
                  Numero    INT           NOT NULL,
                  Fecha     DATETIME      NOT NULL CONSTRAINT DF_ApcWebScript_Fecha DEFAULT (GETDATE()),
                  Quien     NVARCHAR(100) NULL,
                  Resultado NVARCHAR(400) NULL);");

    // ------------------------------------------------------------------ correr

    /// <summary>
    /// Corre un script sobre una base y lo deja anotado. Los scripts son idempotentes,
    /// así que volver a correr uno no rompe nada, pero el panel igual avisa.
    /// </summary>
    public async Task<string> CorrerAsync(string cs, ScriptSql script, string quien)
    {
        string texto;
        try { texto = await File.ReadAllTextAsync(script.Ruta); }
        catch (Exception ex) { return "No se pudo leer el script: " + ex.Message; }

        try
        {
            using var cn = await Db.AbrirAsync(cs);
            await CrearRegistroAsync(cn);

            // SQL Server no entiende GO: es un separador del cliente.
            foreach (var lote in PartirEnLotes(texto))
                await cn.ExecuteAsync(lote, commandTimeout: 600);

            await AnotarAsync(cn, script, quien, "Corrido desde el panel");
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetBaseException().Message;
        }
    }

    /// <summary>Anota un script como corrido sin ejecutarlo: para las bases que ya lo tenían de antes.</summary>
    public async Task<string> MarcarAsync(string cs, ScriptSql script, string quien, string nota = "Marcado a mano")
    {
        try
        {
            using var cn = await Db.AbrirAsync(cs);
            await CrearRegistroAsync(cn);
            await AnotarAsync(cn, script, quien, nota);
            return null;
        }
        catch (Exception ex) { return ex.GetBaseException().Message; }
    }

    /// <summary>Olvida que un script se corrió, para poder volver a correrlo desde el panel.</summary>
    public async Task<string> OlvidarAsync(string cs, string nombreScript)
    {
        try
        {
            using var cn = await Db.AbrirAsync(cs);
            await CrearRegistroAsync(cn);
            await cn.ExecuteAsync("DELETE FROM dbo.ApcWebScript WHERE Nombre = @n", new { n = nombreScript });
            return null;
        }
        catch (Exception ex) { return ex.GetBaseException().Message; }
    }

    private static async Task AnotarAsync(System.Data.IDbConnection cn, ScriptSql s, string quien, string resultado) =>
        await cn.ExecuteAsync(
            @"MERGE dbo.ApcWebScript AS d
              USING (SELECT @nombre AS Nombre) AS o ON d.Nombre = o.Nombre
              WHEN MATCHED THEN UPDATE SET Fecha = GETDATE(), Quien = @quien, Resultado = @resultado
              WHEN NOT MATCHED THEN INSERT (Nombre, Numero, Quien, Resultado)
                   VALUES (@nombre, @numero, @quien, @resultado);",
            new { nombre = s.Nombre, numero = s.Numero, quien, resultado });

    /// <summary>Corre todos los pendientes de una base, en orden. Se detiene en el primero que falla.</summary>
    public async Task<(int Corridos, string Error)> CorrerPendientesAsync(EstadoBase estado, string quien)
    {
        var corridos = 0;
        foreach (var p in estado.Scripts.Where(x => !x.Corrido).OrderBy(x => x.Script.Numero))
        {
            var error = await CorrerAsync(estado.ConnectionString, p.Script, quien);
            if (error != null) return (corridos, $"{p.Script.Nombre}: {error}");
            corridos++;
        }
        return (corridos, null);
    }

    /// <summary>Parte el texto por las líneas GO, como hace SSMS.</summary>
    public static List<string> PartirEnLotes(string texto)
    {
        var lotes = Regex.Split(texto ?? "", @"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return lotes.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
    }
}

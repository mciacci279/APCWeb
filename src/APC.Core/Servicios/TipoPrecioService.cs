using APC.Core.Datos;
using APC.Core.Modelos;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Tipos de precio de la empresa y su relación con las formas de pago (script 18).
/// Mientras el script no se haya corrido, todo devuelve vacío y el sistema sigue
/// funcionando con los recargos y descuentos propios de cada forma de pago.
/// </summary>
public class TipoPrecioService
{
    public async Task<bool> DisponibleAsync(string cs)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN OBJECT_ID(N'dbo.TipoPrecio', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
    }

    public async Task<List<TipoPrecio>> ListarAsync(string cs, bool incluirInactivos = true)
    {
        using var cn = await Db.AbrirAsync(cs);
        if (await cn.ExecuteScalarAsync<int>(
                "SELECT CASE WHEN OBJECT_ID(N'dbo.TipoPrecio', N'U') IS NULL THEN 0 ELSE 1 END") == 0)
            return new List<TipoPrecio>();

        return (await cn.QueryAsync<TipoPrecio>(
            $@"SELECT t.Id, t.Nombre, t.Ajuste, t.Activo, t.Orden,
                      (SELECT COUNT(*) FROM dbo.FormaPago f WHERE f.TipoPrecioId = t.Id) AS FormasPago
               FROM dbo.TipoPrecio t
               {(incluirInactivos ? "" : "WHERE t.Activo = 1")}
               ORDER BY t.Orden, t.Nombre")).ToList();
    }

    public async Task<string> GuardarAsync(string cs, TipoPrecio t)
    {
        t.Nombre = t.Nombre?.Trim();
        if (string.IsNullOrWhiteSpace(t.Nombre)) return "Escribí el nombre del tipo de precio.";
        if (t.Ajuste < -90 || t.Ajuste > 300) return "El ajuste tiene que estar entre -90 % y 300 %.";

        using var cn = await Db.AbrirAsync(cs);
        var repetido = await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.TipoPrecio WHERE Nombre = @Nombre AND Id <> @Id", new { t.Nombre, t.Id });
        if (repetido > 0) return "Ya hay otro tipo de precio con ese nombre.";

        if (t.EsNuevo)
        {
            t.Id = Guid.NewGuid();
            t.Orden = (await cn.ExecuteScalarAsync<int?>("SELECT MAX(Orden) FROM dbo.TipoPrecio") ?? 0) + 1;
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.TipoPrecio (Id, Nombre, Ajuste, Activo, Orden)
                  VALUES (@Id, @Nombre, @Ajuste, 1, @Orden)", t);
        }
        else
        {
            await cn.ExecuteAsync(
                "UPDATE dbo.TipoPrecio SET Nombre = @Nombre, Ajuste = @Ajuste, Activo = @Activo WHERE Id = @Id", t);
        }
        return null;
    }

    public async Task<string> BorrarAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        var usado = await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FormaPago WHERE TipoPrecioId = @id", new { id });
        if (usado > 0) return "No se puede borrar: hay formas de pago usándolo. Cambialas primero o desactivalo.";
        await cn.ExecuteAsync("DELETE FROM dbo.TipoPrecio WHERE Id = @id", new { id });
        return null;
    }

    // ------------------------------------------------------------------ formas de pago

    /// <summary>
    /// Alta y edición de formas de pago. La tabla viene del sistema de escritorio y puede tener
    /// columnas propias de cada instalación, así que el INSERT se arma leyendo las columnas reales:
    /// las que no conocemos y son obligatorias se completan con un valor neutro.
    /// </summary>
    public async Task<string> GuardarFormaPagoAsync(string cs, FormaPagoItem f)
    {
        f.Nombre = f.Nombre?.Trim();
        if (string.IsNullOrWhiteSpace(f.Nombre)) return "Escribí el nombre de la forma de pago.";
        if (f.Nombre.Length > 50) return "El nombre no puede pasar de 50 letras.";

        using var cn = await Db.AbrirAsync(cs);
        var repetido = await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FormaPago WHERE LTRIM(RTRIM(Nombre)) = @Nombre AND Id <> @Id",
            new { f.Nombre, f.Id });
        if (repetido > 0) return "Ya hay otra forma de pago con ese nombre.";

        var columnas = (await cn.QueryAsync<(string Nombre, string Nulable, string Defecto, string Tipo)>(
            @"SELECT COLUMN_NAME, IS_NULLABLE, COLUMN_DEFAULT, DATA_TYPE
              FROM INFORMATION_SCHEMA.COLUMNS
              WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'FormaPago'")).ToList();
        bool Existe(string c) => columnas.Any(x => string.Equals(x.Nombre, c, StringComparison.OrdinalIgnoreCase));

        var p = new DynamicParameters();
        p.Add("Nombre", f.Nombre);
        p.Add("EsCtaCte", f.EsCtaCte);
        p.Add("TipoPrecioId", f.TipoPrecioId);
        p.Add("EsMercadoPago", f.EsMercadoPago);

        if (f.Id == Guid.Empty)
        {
            f.Id = Guid.NewGuid();
            p.Add("Id", f.Id);

            var campos = new List<string> { "Id", "Nombre" };
            var valores = new List<string> { "@Id", "@Nombre" };
            if (Existe("EsCtaCte")) { campos.Add("EsCtaCte"); valores.Add("@EsCtaCte"); }
            if (Existe("TipoPrecioId")) { campos.Add("TipoPrecioId"); valores.Add("@TipoPrecioId"); }
            if (Existe("EsMercadoPago")) { campos.Add("EsMercadoPago"); valores.Add("@EsMercadoPago"); }
            if (Existe("PorcentajeRecargo")) { campos.Add("PorcentajeRecargo"); valores.Add("0"); }
            if (Existe("PorcentajeDescuento")) { campos.Add("PorcentajeDescuento"); valores.Add("0"); }
            if (Existe("Precio")) { campos.Add("Precio"); valores.Add("1"); }

            // Columnas propias de la instalación que son obligatorias y no tienen valor por defecto.
            foreach (var c in columnas)
            {
                if (campos.Any(x => string.Equals(x, c.Nombre, StringComparison.OrdinalIgnoreCase))) continue;
                if (!string.Equals(c.Nulable, "NO", StringComparison.OrdinalIgnoreCase)) continue;
                if (c.Defecto != null) continue;
                campos.Add(c.Nombre);
                valores.Add(NeutroSql(c.Tipo));
            }

            try
            {
                await cn.ExecuteAsync(
                    $"INSERT INTO dbo.FormaPago ({string.Join(", ", campos)}) VALUES ({string.Join(", ", valores)})", p);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex)
            {
                return "No se pudo crear la forma de pago: " + ex.Message;
            }
        }
        else
        {
            p.Add("Id", f.Id);
            var sets = new List<string> { "Nombre = @Nombre" };
            if (Existe("EsCtaCte")) sets.Add("EsCtaCte = @EsCtaCte");
            if (Existe("TipoPrecioId")) sets.Add("TipoPrecioId = @TipoPrecioId");
            if (Existe("EsMercadoPago")) sets.Add("EsMercadoPago = @EsMercadoPago");
            await cn.ExecuteAsync($"UPDATE dbo.FormaPago SET {string.Join(", ", sets)} WHERE Id = @Id", p);
        }
        return null;
    }

    /// <summary>Valor neutro para una columna obligatoria que no conocemos.</summary>
    private static string NeutroSql(string tipo) => (tipo ?? "").ToLowerInvariant() switch
    {
        "bit" => "0",
        "int" or "smallint" or "tinyint" or "bigint" or "decimal" or "numeric" or "money"
            or "smallmoney" or "float" or "real" => "0",
        "date" or "datetime" or "datetime2" or "smalldatetime" => "GETDATE()",
        "uniqueidentifier" => "NEWID()",
        _ => "''"
    };

    /// <summary>Solo se puede borrar una forma de pago que no se usó en ningún comprobante.</summary>
    public async Task<string> BorrarFormaPagoAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        var usada = await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Factura WHERE FormaPagoId = @id", new { id });
        if (usada > 0) return "No se puede borrar: ya se emitieron comprobantes con esta forma de pago.";
        try
        {
            await cn.ExecuteAsync("DELETE FROM dbo.FormaPago WHERE Id = @id", new { id });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            return "No se pudo borrar: está en uso en otra parte del sistema. Detalle: " + ex.Message;
        }
        return null;
    }

    /// <summary>Asocia una forma de pago a un tipo de precio (null = sin tipo, usa el precio de lista).</summary>
    public async Task<string> AsignarAsync(string cs, Guid formaPagoId, Guid? tipoPrecioId)
    {
        try
        {
            using var cn = await Db.AbrirAsync(cs);
            await cn.ExecuteAsync(
                "UPDATE dbo.FormaPago SET TipoPrecioId = @tipoPrecioId WHERE Id = @formaPagoId",
                new { formaPagoId, tipoPrecioId });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            return "No se pudo guardar. ¿Se ejecutó el script 18 en la base de esta empresa? Detalle: " + ex.Message;
        }
        return null;
    }
}

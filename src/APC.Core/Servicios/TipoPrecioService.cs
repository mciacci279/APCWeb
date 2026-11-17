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

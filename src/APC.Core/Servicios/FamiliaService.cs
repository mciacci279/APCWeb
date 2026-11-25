using APC.Core.Datos;
using APC.Core.Modelos;
using Dapper;
using Microsoft.Data.SqlClient;

namespace APC.Core.Servicios;

/// <summary>
/// ABM de familias de productos. En la base es la tabla dbo.Rubro (la misma que usa el sistema de
/// escritorio, que la llama "Rubro"), así que no hace falta ningún script.
/// </summary>
public class FamiliaService
{
    public async Task<List<Familia>> ListarAsync(string cs)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<Familia>(
            @"SELECT r.Id, r.Nombre, r.Observaciones,
                     (SELECT COUNT(*) FROM dbo.Producto p WHERE p.RubroId = r.Id AND ISNULL(p.Activo, 1) = 1) AS Productos,
                     (SELECT COUNT(*) FROM dbo.Producto p WHERE p.RubroId = r.Id AND ISNULL(p.Activo, 1) = 0) AS ProductosBaja
              FROM dbo.Rubro r
              ORDER BY r.Nombre")).ToList();
    }

    /// <summary>Productos activos sin familia (para el aumento masivo y el listado).</summary>
    public async Task<int> SinFamiliaAsync(string cs)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Producto WHERE RubroId IS NULL AND ISNULL(Activo, 1) = 1");
    }

    /// <summary>Crea (Id vacío) o modifica una familia. Devuelve el error o null.</summary>
    public async Task<string> GuardarAsync(string cs, Familia f)
    {
        var nombre = f.Nombre?.Trim();
        if (string.IsNullOrEmpty(nombre)) return "Falta el nombre de la familia.";
        if (nombre.Length > 50) return "El nombre no puede superar los 50 caracteres.";
        var obs = string.IsNullOrWhiteSpace(f.Observaciones) ? null : f.Observaciones.Trim();
        if (obs?.Length > 200) return "Las observaciones no pueden superar los 200 caracteres.";

        using var cn = await Db.AbrirAsync(cs);
        var repetida = await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Rubro WHERE LTRIM(RTRIM(Nombre)) = @nombre AND Id <> @id",
            new { nombre, id = f.Id });
        if (repetida > 0) return $"Ya existe una familia \"{nombre}\".";
        try
        {
            if (f.Id == Guid.Empty)
            {
                f.Id = Guid.NewGuid();
                await cn.ExecuteAsync("INSERT INTO dbo.Rubro (Id, Nombre, Observaciones) VALUES (@Id, @nombre, @obs)",
                    new { f.Id, nombre, obs });
            }
            else
            {
                var filas = await cn.ExecuteAsync("UPDATE dbo.Rubro SET Nombre = @nombre, Observaciones = @obs WHERE Id = @Id",
                    new { f.Id, nombre, obs });
                if (filas == 0) return "La familia ya no existe.";
            }
        }
        catch (SqlException ex) when (ex.Number is 8152 or 2628)
        {
            return "El nombre o las observaciones son demasiado largos para la base.";
        }
        return null;
    }

    /// <summary>
    /// Borra una familia. Sus productos pasan a otra familia (moverA) o quedan sin familia (moverA null).
    /// </summary>
    public async Task<string> EliminarAsync(string cs, Guid id, Guid? moverA)
    {
        if (moverA == id) return "Elegí otra familia para mover los productos.";
        using var cn = await Db.AbrirAsync(cs);
        using var tx = cn.BeginTransaction();
        try
        {
            await cn.ExecuteAsync("UPDATE dbo.Producto SET RubroId = @moverA WHERE RubroId = @id", new { id, moverA }, tx);
            var filas = await cn.ExecuteAsync("DELETE FROM dbo.Rubro WHERE Id = @id", new { id }, tx);
            if (filas == 0) { tx.Rollback(); return "La familia ya no existe."; }
            tx.Commit();
            return null;
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            tx.Rollback();
            return "No se puede borrar: la familia está usada en otra parte del sistema (por ejemplo, en el sistema de escritorio). Podés renombrarla.";
        }
    }
}

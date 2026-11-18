using APC.Core.Datos;
using APC.Core.Modelos;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Alta, baja y modificación de clientes sobre la tabla dbo.Cliente, la misma que usa APC.WinForms.
/// </summary>
public class ClienteService
{
    private const string Columnas =
        @"c.Id, c.Apellido, c.Nombre, c.NroDocumento, c.Domicilio, c.Localidad,
          c.Telefono1, c.Email, td.Nombre AS TipoDocumento, i.Nombre AS CondicionIva";

    private const string Desde =
        @"FROM dbo.Cliente c
          LEFT JOIN dbo.TipoDocumento td ON td.Id = c.TipoDocumentoId
          LEFT JOIN dbo.Iva i ON i.Id = c.IvaId";

    public async Task<List<ClienteItem>> BuscarAsync(string cs, string texto, int max = 50)
    {
        using var cn = await Db.AbrirAsync(cs);
        if (string.IsNullOrWhiteSpace(texto))
            return (await cn.QueryAsync<ClienteItem>(
                $"SELECT TOP (@max) {Columnas} {Desde} ORDER BY c.Apellido, c.Nombre", new { max })).ToList();

        var like = "%" + texto.Trim().Replace("[", "[[]").Replace("%", "[%]") + "%";
        return (await cn.QueryAsync<ClienteItem>(
            $@"SELECT TOP (@max) {Columnas} {Desde}
               WHERE c.Apellido LIKE @like OR c.Nombre LIKE @like OR c.NroDocumento LIKE @like
               ORDER BY c.Apellido, c.Nombre", new { like, max })).ToList();
    }

    public async Task<ClienteDetalle> ObtenerAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.QueryFirstOrDefaultAsync<ClienteDetalle>(
            @"SELECT Id, Apellido, Nombre, TipoDocumentoId, NroDocumento, IvaId, Domicilio, NroCalle,
                     Barrio, Localidad, Cp, IngresosBrutos, Telefono1, Telefono2, Email
              FROM dbo.Cliente WHERE Id = @id", new { id });
    }

    /// <summary>
    /// Busca un cliente por documento, ignorando guiones y espacios. Se usa antes de ir a ARCA:
    /// si ya está cargado no tiene sentido consultar el padrón ni crearlo de nuevo.
    /// </summary>
    public async Task<ClienteDetalle> PorDocumentoAsync(string cs, string nroDocumento)
    {
        var doc = new string((nroDocumento ?? "").Where(char.IsDigit).ToArray());
        if (doc.Length < 7) return null;

        using var cn = await Db.AbrirAsync(cs);
        return await cn.QueryFirstOrDefaultAsync<ClienteDetalle>(
            @"SELECT TOP 1 Id, Apellido, Nombre, TipoDocumentoId, NroDocumento, IvaId, Domicilio, NroCalle,
                     Barrio, Localidad, Cp, IngresosBrutos, Telefono1, Telefono2, Email
              FROM dbo.Cliente
              WHERE REPLACE(REPLACE(ISNULL(NroDocumento, ''), '-', ''), ' ', '') = @doc
              ORDER BY Apellido, Nombre", new { doc });
    }

    public async Task<List<OpcionItem>> TiposDocumentoAsync(string cs)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<OpcionItem>("SELECT Id, Nombre FROM dbo.TipoDocumento ORDER BY Nombre")).ToList();
    }

    public async Task<List<OpcionItem>> CondicionesIvaAsync(string cs)
    {
        using var cn = await Db.AbrirAsync(cs);
        return (await cn.QueryAsync<OpcionItem>("SELECT Id, Nombre FROM dbo.Iva ORDER BY Nombre")).ToList();
    }

    /// <summary>Crea o actualiza el cliente. Devuelve el error, o null si salió bien.</summary>
    public async Task<string> GuardarAsync(string cs, ClienteDetalle c)
    {
        c.Apellido = Recortar(c.Apellido, 50);
        c.Nombre = Recortar(c.Nombre, 50);
        c.NroDocumento = Recortar(c.NroDocumento?.Replace("-", "").Replace(".", "").Trim(), 13);
        c.Domicilio = Recortar(c.Domicilio, 100);
        c.NroCalle = Recortar(c.NroCalle, 10);
        c.Barrio = Recortar(c.Barrio, 50);
        c.Localidad = Recortar(c.Localidad, 150);
        c.Cp = Recortar(c.Cp, 10);
        c.IngresosBrutos = Recortar(c.IngresosBrutos, 50);
        c.Telefono1 = Recortar(c.Telefono1, 50);
        c.Telefono2 = Recortar(c.Telefono2, 50);
        c.Email = Recortar(c.Email, 100);

        if (string.IsNullOrWhiteSpace(c.Apellido) && string.IsNullOrWhiteSpace(c.Nombre))
            return "Escribí el apellido o la razón social del cliente.";
        if (!string.IsNullOrWhiteSpace(c.Email) && !c.Email.Contains('@'))
            return "El email no parece válido.";

        using var cn = await Db.AbrirAsync(cs);

        if (!string.IsNullOrWhiteSpace(c.NroDocumento))
        {
            var repetido = await cn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.Cliente WHERE NroDocumento = @doc AND Id <> @id",
                new { doc = c.NroDocumento, id = c.Id });
            if (repetido > 0) return "Ya hay otro cliente con ese número de documento.";
        }

        if (c.EsNuevo)
        {
            c.Id = Guid.NewGuid();
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.Cliente
                    (Id, Apellido, Nombre, TipoDocumentoId, NroDocumento, IvaId, Domicilio, NroCalle,
                     Barrio, Localidad, Cp, IngresosBrutos, Telefono1, Telefono2, Email)
                  VALUES
                    (@Id, @Apellido, @Nombre, @TipoDocumentoId, @NroDocumento, @IvaId, @Domicilio, @NroCalle,
                     @Barrio, @Localidad, @Cp, @IngresosBrutos, @Telefono1, @Telefono2, @Email)", c);
        }
        else
        {
            await cn.ExecuteAsync(
                @"UPDATE dbo.Cliente SET
                    Apellido = @Apellido, Nombre = @Nombre, TipoDocumentoId = @TipoDocumentoId,
                    NroDocumento = @NroDocumento, IvaId = @IvaId, Domicilio = @Domicilio, NroCalle = @NroCalle,
                    Barrio = @Barrio, Localidad = @Localidad, Cp = @Cp, IngresosBrutos = @IngresosBrutos,
                    Telefono1 = @Telefono1, Telefono2 = @Telefono2, Email = @Email
                  WHERE Id = @Id", c);
        }
        return null;
    }

    /// <summary>Borra el cliente, salvo que ya tenga comprobantes: en ese caso no se puede.</summary>
    public async Task<string> EliminarAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        var usos = await cn.ExecuteScalarAsync<int>(
            @"SELECT (SELECT COUNT(*) FROM dbo.Factura WHERE ClienteId = @id)
                   + (SELECT COUNT(*) FROM dbo.NotaCredito WHERE ClienteId = @id)
                   + (SELECT COUNT(*) FROM dbo.Presupuesto WHERE ClienteId = @id)", new { id });
        if (usos > 0)
            return $"No se puede borrar: el cliente tiene {usos} comprobante(s). Podés modificar sus datos, pero no eliminarlo.";

        try
        {
            await cn.ExecuteAsync("DELETE FROM dbo.Cliente WHERE Id = @id", new { id });
            return null;
        }
        catch (Exception ex)
        {
            return "No se pudo borrar el cliente: " + ex.Message;
        }
    }

    private static string Recortar(string texto, int largo)
    {
        texto = texto?.Trim();
        if (string.IsNullOrEmpty(texto)) return null;
        return texto.Length <= largo ? texto : texto[..largo];
    }
}

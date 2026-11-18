using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Tenants;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Cobranzas de CM Tech: el abono mensual que se le cobra a cada empresa.
/// La factura fiscal la emite cada integrante por su cuenta; acá se anota su número,
/// el aviso que se le mandó al cliente y cuándo pagó.
/// </summary>
public class CobranzaService
{
    private readonly TenantRegistry _tenants;
    public CobranzaService(TenantRegistry tenants) => _tenants = tenants;

    private string Central => _tenants.Central;

    public static string PeriodoDe(DateTime fecha) => fecha.ToString("yyyy-MM");

    private const string Columnas = @"
        c.Id, c.EmpresaId, c.Periodo, c.Concepto, c.Monto, c.FacturadorId, c.ComprobanteNro, c.Estado,
        c.FechaAlta, c.FechaEnvio, c.FechaPago, c.MedioPago, c.Observaciones,
        e.Codigo AS EmpresaCodigo, e.Nombre AS EmpresaNombre, e.NombreFantasia AS EmpresaFantasia,
        e.ContactoNombre, e.ContactoTelefono, e.ContactoEmail,
        f.Nombre AS Facturador, f.Alias AS FacturadorDatos,
        c.TipoComprobanteAFIP, c.PuntoVenta, c.Numero, c.CAE, c.CAEVencimiento, c.FechaEmision,
        ua.NombreUsuario AS CreadoPorNombre, ue.NombreUsuario AS EnviadoPorNombre, uc.NombreUsuario AS CobradoPorNombre,
        (SELECT COUNT(*) FROM dbo.CobroEnvio ce WHERE ce.CobroId = c.Id) AS Avisos";

    private const string Desde = @"
        FROM dbo.Cobro c
        JOIN dbo.Empresa e ON e.Id = c.EmpresaId
        LEFT JOIN dbo.Facturador f ON f.Id = c.FacturadorId
        LEFT JOIN dbo.Usuario ua ON ua.Id = c.CreadoPor
        LEFT JOIN dbo.Usuario ue ON ue.Id = c.EnviadoPor
        LEFT JOIN dbo.Usuario uc ON uc.Id = c.CobradoPor";

    // ------------------------------------------------------------------ facturadores

    public async Task<List<Facturador>> FacturadoresAsync(bool soloActivos = false)
    {
        using var cn = await Db.AbrirAsync(Central);
        var filtro = soloActivos ? "WHERE Activo = 1" : "";
        try
        {
            return (await cn.QueryAsync<Facturador>(
                $@"SELECT Id, Nombre, Cuit, Alias, Cbu, Banco, Activo,
                          PuntoVenta, Ambiente, CertificadoPath, ClavePath,
                          Domicilio, Localidad, IngresosBrutos, FechaInicioActividades
                   FROM dbo.Facturador {filtro} ORDER BY Nombre")).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            // Todavía no se corrió el script 11 (facturación propia)
            return (await cn.QueryAsync<Facturador>(
                $"SELECT Id, Nombre, Cuit, Alias, Cbu, Banco, Activo FROM dbo.Facturador {filtro} ORDER BY Nombre")).ToList();
        }
    }

    public async Task<string> GuardarFacturadorAsync(Facturador f)
    {
        f.Nombre = f.Nombre?.Trim();
        if (string.IsNullOrWhiteSpace(f.Nombre)) return "Escribí el nombre.";
        f.Cuit = Limpio(f.Cuit, 13);
        f.Alias = Limpio(f.Alias, 60);
        f.Cbu = Limpio(f.Cbu, 30);
        f.Banco = Limpio(f.Banco, 60);

        f.Domicilio = Limpio(f.Domicilio, 150);
        f.Localidad = Limpio(f.Localidad, 100);
        f.IngresosBrutos = Limpio(f.IngresosBrutos, 50);
        f.Ambiente = string.IsNullOrWhiteSpace(f.Ambiente) ? "HOMO" : f.Ambiente.Trim().ToUpperInvariant();
        if (f.Ambiente != "HOMO" && f.Ambiente != "PROD") return "Ambiente inválido.";
        if (f.PuntoVenta is <= 0) f.PuntoVenta = null;
        if (f.PuntoVenta > 99999) return "El punto de venta debe estar entre 1 y 99999.";

        using var cn = await Db.AbrirAsync(Central);
        try
        {
            if (f.EsNuevo)
            {
                f.Id = Guid.NewGuid();
                await cn.ExecuteAsync(
                    @"INSERT INTO dbo.Facturador
                        (Id, Nombre, Cuit, Alias, Cbu, Banco, Activo,
                         PuntoVenta, Ambiente, CertificadoPath, ClavePath,
                         Domicilio, Localidad, IngresosBrutos, FechaInicioActividades)
                      VALUES
                        (@Id, @Nombre, @Cuit, @Alias, @Cbu, @Banco, 1,
                         @PuntoVenta, @Ambiente, @CertificadoPath, @ClavePath,
                         @Domicilio, @Localidad, @IngresosBrutos, @FechaInicioActividades)", f);
            }
            else
            {
                await cn.ExecuteAsync(
                    @"UPDATE dbo.Facturador SET
                        Nombre = @Nombre, Cuit = @Cuit, Alias = @Alias, Cbu = @Cbu, Banco = @Banco, Activo = @Activo,
                        PuntoVenta = @PuntoVenta, Ambiente = @Ambiente,
                        CertificadoPath = @CertificadoPath, ClavePath = @ClavePath,
                        Domicilio = @Domicilio, Localidad = @Localidad,
                        IngresosBrutos = @IngresosBrutos, FechaInicioActividades = @FechaInicioActividades
                      WHERE Id = @Id", f);
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            return "No se pudieron guardar los datos de facturación. ¿Se ejecutó el script 11 en APCWeb_Central? Detalle: " + ex.Message;
        }
        return null;
    }

    /// <summary>Quién factura habitualmente a cada empresa.</summary>
    public async Task AsignarFacturadorAsync(Guid empresaId, Guid? facturadorId)
    {
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync("UPDATE dbo.Empresa SET FacturadorId = @facturadorId WHERE Id = @empresaId",
            new { empresaId, facturadorId });
    }

    public async Task GuardarContactoAsync(Guid empresaId, string nombre, string telefono, string email)
    {
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync(
            @"UPDATE dbo.Empresa SET ContactoNombre = @nombre, ContactoTelefono = @telefono, ContactoEmail = @email
              WHERE Id = @empresaId",
            new { empresaId, nombre = Limpio(nombre, 100), telefono = Limpio(telefono, 50), email = Limpio(email, 100) });
    }

    // ------------------------------------------------------------------ cobros del mes

    /// <summary>
    /// Crea los cobros del mes para las empresas activas que tengan monto de abono y todavía
    /// no tengan el cobro de ese período. Devuelve cuántos creó.
    /// </summary>
    public async Task<(int Creados, string Error)> GenerarMesAsync(string periodo, Guid usuarioId)
    {
        if (!DateTime.TryParse(periodo + "-01", out _)) return (0, "El período no es válido.");

        using var cn = await Db.AbrirAsync(Central);
        try
        {
            // Los cobros anulados no cuentan: si se anuló el del mes, se puede generar de nuevo.
            var creados = await cn.ExecuteAsync(
                @"INSERT INTO dbo.Cobro (Id, EmpresaId, Periodo, Concepto, Monto, FacturadorId, Estado, FechaAlta, CreadoPor)
                  SELECT NEWID(), e.Id, @periodo, @concepto, e.AbonoMonto, e.FacturadorId, 'PENDIENTE', GETDATE(), @usuarioId
                  FROM dbo.Empresa e
                  WHERE e.Activa = 1 AND ISNULL(e.AbonoMonto, 0) > 0
                    AND NOT EXISTS (SELECT 1 FROM dbo.Cobro c
                                    WHERE c.EmpresaId = e.Id AND c.Periodo = @periodo AND c.Estado <> 'ANULADO')",
                new { periodo, usuarioId, concepto = "Abono APC Web · " + Cobro.TextoDePeriodo(periodo) });
            return (creados, null);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return (0, "Para poder rehacer un cobro anulado hay que ejecutar el script 12 en APCWeb_Central.");
        }
    }

    public async Task<List<Cobro>> ListarAsync(string periodo, string estado = null, Guid? facturadorId = null)
    {
        using var cn = await Db.AbrirAsync(Central);
        return (await cn.QueryAsync<Cobro>(
            $@"SELECT {Columnas} {Desde}
               WHERE c.Periodo = @periodo
                 AND (@estado IS NULL OR c.Estado = @estado)
                 AND (@facturadorId IS NULL OR c.FacturadorId = @facturadorId)
               ORDER BY e.Nombre",
            new { periodo, estado, facturadorId })).ToList();
    }

    /// <summary>Todos los cobros de una empresa, del más nuevo al más viejo.</summary>
    public async Task<List<Cobro>> HistorialAsync(Guid empresaId, int max = 36)
    {
        using var cn = await Db.AbrirAsync(Central);
        return (await cn.QueryAsync<Cobro>(
            $"SELECT TOP (@max) {Columnas} {Desde} WHERE c.EmpresaId = @empresaId ORDER BY c.Periodo DESC",
            new { empresaId, max })).ToList();
    }

    public async Task<ResumenCobranzas> ResumenAsync(string periodo)
    {
        using var cn = await Db.AbrirAsync(Central);
        var r = await cn.QueryFirstAsync<ResumenCobranzas>(
            @"SELECT @periodo AS Periodo,
                     COUNT(*) AS Cantidad,
                     ISNULL(SUM(Monto), 0) AS Total,
                     SUM(CASE WHEN Estado = 'ABONADO' THEN 1 ELSE 0 END) AS CantidadAbonada,
                     ISNULL(SUM(CASE WHEN Estado = 'ABONADO' THEN Monto ELSE 0 END), 0) AS Cobrado,
                     SUM(CASE WHEN Estado = 'PENDIENTE' THEN 1 ELSE 0 END) AS SinAvisar
              FROM dbo.Cobro WHERE Periodo = @periodo AND Estado <> 'ANULADO'",
            new { periodo });
        return r;
    }

    public async Task<List<string>> PeriodosAsync()
    {
        using var cn = await Db.AbrirAsync(Central);
        return (await cn.QueryAsync<string>("SELECT DISTINCT Periodo FROM dbo.Cobro ORDER BY Periodo DESC")).ToList();
    }

    // ------------------------------------------------------------------ acciones sobre un cobro

    public async Task<string> GuardarAsync(Guid id, decimal monto, Guid? facturadorId, string comprobanteNro, string observaciones)
    {
        if (monto < 0) return "El monto no puede ser negativo.";
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync(
            @"UPDATE dbo.Cobro SET Monto = @monto, FacturadorId = @facturadorId,
                     ComprobanteNro = @comprobanteNro, Observaciones = @observaciones
              WHERE Id = @id",
            new { id, monto, facturadorId, comprobanteNro = Limpio(comprobanteNro, 40), observaciones = Limpio(observaciones, 300) });
        return null;
    }

    /// <summary>Deja registrado que se le avisó al cliente (y por qué medio).</summary>
    public async Task RegistrarEnvioAsync(Guid cobroId, Guid usuarioId, string medio, string destino, string nota = null)
    {
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync(
            @"INSERT INTO dbo.CobroEnvio (Id, CobroId, Fecha, UsuarioId, Medio, Destino, Nota)
              VALUES (NEWID(), @cobroId, GETDATE(), @usuarioId, @medio, @destino, @nota);

              UPDATE dbo.Cobro SET FechaEnvio = GETDATE(), EnviadoPor = @usuarioId,
                     Estado = CASE WHEN Estado = 'PENDIENTE' THEN 'ENVIADO' ELSE Estado END
              WHERE Id = @cobroId;",
            new { cobroId, usuarioId, medio = Limpio(medio, 20), destino = Limpio(destino, 100), nota = Limpio(nota, 200) });
    }

    public async Task<List<EnvioCobro>> EnviosAsync(Guid cobroId)
    {
        using var cn = await Db.AbrirAsync(Central);
        return (await cn.QueryAsync<EnvioCobro>(
            @"SELECT ce.Fecha, ce.Medio, ce.Destino, ce.Nota, u.NombreUsuario AS Usuario
              FROM dbo.CobroEnvio ce LEFT JOIN dbo.Usuario u ON u.Id = ce.UsuarioId
              WHERE ce.CobroId = @cobroId ORDER BY ce.Fecha DESC", new { cobroId })).ToList();
    }

    /// <summary>
    /// Marca el cobro como abonado y, si se pide, corre un mes el vencimiento del abono de la empresa,
    /// que es lo que decide si el sistema le sigue funcionando al cliente.
    /// </summary>
    public async Task<string> MarcarAbonadoAsync(Guid cobroId, Guid usuarioId, DateTime fechaPago,
        string medioPago, string comprobanteNro, bool extenderAbono = true)
    {
        using var cn = await Db.AbrirAsync(Central);
        var cobro = await cn.QueryFirstOrDefaultAsync<(Guid EmpresaId, string Periodo, string Estado)>(
            "SELECT EmpresaId, Periodo, Estado FROM dbo.Cobro WHERE Id = @cobroId", new { cobroId });
        if (cobro == default) return "El cobro no existe.";
        if (cobro.Estado == EstadoCobro.Abonado) return "Ese cobro ya figura abonado.";

        await cn.ExecuteAsync(
            @"UPDATE dbo.Cobro SET Estado = 'ABONADO', FechaPago = @fechaPago, MedioPago = @medioPago,
                     CobradoPor = @usuarioId, ComprobanteNro = ISNULL(@comprobanteNro, ComprobanteNro)
              WHERE Id = @cobroId",
            new { cobroId, usuarioId, fechaPago = fechaPago.Date, medioPago = Limpio(medioPago, 40), comprobanteNro = Limpio(comprobanteNro, 40) });

        if (extenderAbono)
        {
            // El abono queda pago hasta el último día del período cobrado (o un mes más, si ya estaba al día).
            var hastaPeriodo = DateTime.TryParse(cobro.Periodo + "-01", out var m)
                ? m.AddMonths(1).AddDays(-1)
                : DateTime.Today;
            await cn.ExecuteAsync(
                @"UPDATE dbo.Empresa
                  SET AbonoPagoHasta = CASE WHEN AbonoPagoHasta IS NULL OR AbonoPagoHasta < @hasta THEN @hasta ELSE AbonoPagoHasta END
                  WHERE Id = @empresaId",
                new { empresaId = cobro.EmpresaId, hasta = hastaPeriodo.Date });
            _tenants.Invalidar();
        }
        return null;
    }

    public async Task<string> DesmarcarAsync(Guid cobroId)
    {
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync(
            @"UPDATE dbo.Cobro SET Estado = CASE WHEN FechaEnvio IS NULL THEN 'PENDIENTE' ELSE 'ENVIADO' END,
                     FechaPago = NULL, MedioPago = NULL, CobradoPor = NULL
              WHERE Id = @cobroId", new { cobroId });
        return null;
    }

    public async Task AnularAsync(Guid cobroId, string motivo)
    {
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync(
            "UPDATE dbo.Cobro SET Estado = 'ANULADO', Observaciones = @motivo WHERE Id = @cobroId",
            new { cobroId, motivo = Limpio(motivo, 300) });
    }

    private static string Limpio(string texto, int largo)
    {
        texto = texto?.Trim();
        if (string.IsNullOrEmpty(texto)) return null;
        return texto.Length <= largo ? texto : texto[..largo];
    }
}

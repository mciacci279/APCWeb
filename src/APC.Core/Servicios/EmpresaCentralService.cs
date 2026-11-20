using System.Text.RegularExpressions;
using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Tenants;
using Dapper;
using Microsoft.Data.SqlClient;

namespace APC.Core.Servicios;

/// <summary>Alta, edición y suspensión de empresas en la base central (panel de CM Tech).</summary>
public class EmpresaCentralService
{
    private static readonly Regex CodigoValido = new("^[a-z0-9][a-z0-9-]{1,29}$");
    private readonly TenantRegistry _tenants;

    public const int LogoMaximoBytes = 500 * 1024;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _logos = new();

    public EmpresaCentralService(TenantRegistry tenants) => _tenants = tenants;

    // ------------------------------------------------------------------ logo

    /// <summary>Guarda el logo de la empresa. Solo PNG o JPG (se verifica el contenido, no la extensión).</summary>
    public async Task<string> GuardarLogoAsync(Guid empresaId, byte[] datos)
    {
        if (datos == null || datos.Length == 0) return "El archivo está vacío.";
        if (datos.Length > LogoMaximoBytes) return "El logo no puede superar los 500 KB. Achicá la imagen.";
        var tipo = TipoImagen(datos);
        if (tipo == null) return "El logo tiene que ser una imagen PNG o JPG.";

        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync("UPDATE dbo.Empresa SET Logo = @datos, LogoTipo = @tipo WHERE Id = @id",
            new { datos, tipo, id = empresaId });
        _logos.TryRemove(empresaId, out _);
        return null;
    }

    public async Task QuitarLogoAsync(Guid empresaId)
    {
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync("UPDATE dbo.Empresa SET Logo = NULL, LogoTipo = NULL WHERE Id = @id", new { id = empresaId });
        _logos.TryRemove(empresaId, out _);
    }

    /// <summary>Logo como data URI para insertar en la página de impresión (null si no tiene). Se cachea en memoria.</summary>
    public async Task<string> LogoDataUriAsync(Guid empresaId)
    {
        if (_logos.TryGetValue(empresaId, out var cache)) return cache == "" ? null : cache;
        using var cn = await Db.AbrirAsync(Central);
        var fila = await cn.QueryFirstOrDefaultAsync<(byte[] Logo, string LogoTipo)>(
            "SELECT Logo, LogoTipo FROM dbo.Empresa WHERE Id = @id", new { id = empresaId });
        var uri = fila.Logo == null || fila.Logo.Length == 0
            ? ""
            : $"data:{fila.LogoTipo ?? "image/png"};base64,{Convert.ToBase64String(fila.Logo)}";
        _logos[empresaId] = uri;
        return uri == "" ? null : uri;
    }

    private static string TipoImagen(byte[] d)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        return null;
    }

    private string Central => _tenants.Central;

    public async Task<List<TenantConfig>> ListarAsync()
    {
        using var cn = await Db.AbrirAsync(Central);
        const string sql =
            @"SELECT e.Id, e.Codigo, e.Nombre, e.NombreFantasia, e.ConnectionString, e.PuntoVenta, e.Ambiente, e.CertificadoPath, e.ClavePath,
                     e.Concepto, e.Moneda, e.Activa, e.MotivoSuspension, e.FechaAlta, e.LeyendaTicket,
                     (SELECT COUNT(*) FROM dbo.Usuario u WHERE u.EmpresaId = e.Id AND u.Activo = 1) AS CantidadUsuarios,
                     CAST(CASE WHEN e.Logo IS NULL THEN 0 ELSE 1 END AS BIT) AS TieneLogo {0}
              FROM dbo.Empresa e ORDER BY e.Nombre";
        try
        {
            return (await cn.QueryAsync<TenantConfig>(
                string.Format(sql, @", e.AbonoPagoHasta, e.AbonoDiasGracia, e.AbonoMonto, e.AbonoNotas,
                                      e.Modulos, e.ModulosSupervisor, e.ModulosEmpleado"))).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            try
            {
                // Sin el script 19 (módulos por rol)
                return (await cn.QueryAsync<TenantConfig>(
                    string.Format(sql, ", e.AbonoPagoHasta, e.AbonoDiasGracia, e.AbonoMonto, e.AbonoNotas, e.Modulos"))).ToList();
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
            try
            {
                // Todavía no se corrió el script 17 (módulos): las empresas ven todo
                return (await cn.QueryAsync<TenantConfig>(
                    string.Format(sql, ", e.AbonoPagoHasta, e.AbonoDiasGracia, e.AbonoMonto, e.AbonoNotas"))).ToList();
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                // Tampoco el script 09 (abono)
                return (await cn.QueryAsync<TenantConfig>(string.Format(sql, ""))).ToList();
            }
            }
        }
    }

    /// <summary>
    /// Módulos que la empresa puede ver, separados por coma. Vacío = todos, que es lo que
    /// venían teniendo las empresas antes de esta configuración.
    /// </summary>
    public async Task<string> GuardarModulosAsync(Guid empresaId, string modulos, string supervisor, string empleado)
    {
        string Limpio(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        modulos = Limpio(modulos);
        supervisor = Limpio(supervisor);
        empleado = Limpio(empleado);
        if (modulos?.Length > 200 || supervisor?.Length > 200 || empleado?.Length > 200) return "Demasiados módulos.";
        try
        {
            using var cn = await Db.AbrirAsync(Central);
            await cn.ExecuteAsync(
                @"UPDATE dbo.Empresa SET Modulos = @modulos, ModulosSupervisor = @supervisor,
                         ModulosEmpleado = @empleado WHERE Id = @empresaId",
                new { empresaId, modulos, supervisor, empleado });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            return "No se pudieron guardar los módulos. ¿Se ejecutaron los scripts 17 y 19 en APCWeb_Central? Detalle: " + ex.Message;
        }
        _tenants.Invalidar();
        return null;
    }

    // ------------------------------------------------------------------ abono

    /// <summary>Guarda hasta qué día está pago el abono, los días de gracia, el monto y las notas.</summary>
    public async Task<string> GuardarAbonoAsync(Guid empresaId, DateTime? pagoHasta, int diasGracia, decimal? monto, string notas)
    {
        if (diasGracia < 0 || diasGracia > 90) return "Los días de gracia deben estar entre 0 y 90.";
        if (monto < 0) return "El monto no puede ser negativo.";
        notas = string.IsNullOrWhiteSpace(notas) ? null : notas.Trim();
        if (notas?.Length > 300) notas = notas[..300];
        try
        {
            using var cn = await Db.AbrirAsync(Central);
            await cn.ExecuteAsync(
                @"UPDATE dbo.Empresa SET AbonoPagoHasta = @pagoHasta, AbonoDiasGracia = @diasGracia,
                         AbonoMonto = @monto, AbonoNotas = @notas WHERE Id = @empresaId",
                new { empresaId, pagoHasta = pagoHasta?.Date, diasGracia, monto, notas });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            return "No se pudo guardar el abono. ¿Se ejecutó el script 09 en APCWeb_Central? Detalle: " + ex.Message;
        }
        _tenants.Invalidar();
        return null;
    }

    // ------------------------------------------------------------------ alta automática de la base

    private static readonly Regex NombreBaseValido = new("^[A-Za-z][A-Za-z0-9_]{2,59}$");

    /// <summary>
    /// Crea la base de una empresa nueva copiando la base plantilla (backup + restore en el mismo
    /// SQL Server de la base central) y la deja sin datos. Devuelve la cadena de conexión de la
    /// base creada; si viene con texto en el segundo campo pero con cadena, la base quedó creada
    /// y lo que falló fue la limpieza.
    /// El usuario SQL de la conexión central necesita permiso para crear bases (dbcreator o sysadmin).
    /// </summary>
    public async Task<(string ConnectionString, string Error)> CrearBaseDesdePlantillaAsync(string nombreBase)
    {
        nombreBase = nombreBase?.Trim();
        if (nombreBase == null || !NombreBaseValido.IsMatch(nombreBase))
            return (null, "El nombre de la base debe empezar con una letra y tener solo letras, números o guion bajo (3 a 60 caracteres).");

        var plantilla = _tenants.BasePlantilla;
        if (string.IsNullOrWhiteSpace(plantilla) || !NombreBaseValido.IsMatch(plantilla))
            return (null, "No está configurada la base plantilla (APC:BasePlantilla).");
        if (string.Equals(plantilla, nombreBase, StringComparison.OrdinalIgnoreCase))
            return (null, "La base nueva no puede llamarse igual que la plantilla.");

        try
        {
            using var cn = await Db.AbrirAsync(Central);

            if (await cn.ExecuteScalarAsync<int?>("SELECT DB_ID(@n)", new { n = plantilla }) == null)
                return (null, $"No existe la base plantilla «{plantilla}» en el servidor. Creala (una base limpia, con los scripts 01 y 07 aplicados) o cambiá APC:BasePlantilla.");
            if (await cn.ExecuteScalarAsync<int?>("SELECT DB_ID(@n)", new { n = nombreBase }) != null)
                return (null, $"Ya existe una base llamada «{nombreBase}».");

            var carpeta = (await cn.ExecuteScalarAsync<string>("SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS NVARCHAR(400))"))?.TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(carpeta))
                return (null, "No se pudo averiguar la carpeta de datos del SQL Server.");
            var respaldo = carpeta + "\\apcweb_plantilla.bak";

            // Los nombres ya están validados contra NombreBaseValido: no pueden traer comillas ni corchetes.
            await cn.ExecuteAsync(
                $"BACKUP DATABASE [{plantilla}] TO DISK = @respaldo WITH COPY_ONLY, INIT", new { respaldo }, commandTimeout: 600);

            var archivos = (await cn.QueryAsync("RESTORE FILELISTONLY FROM DISK = @respaldo", new { respaldo })).ToList();
            var mover = new List<string>();
            int nLog = 0, nDatos = 0;
            foreach (var a in archivos)
            {
                string logico = a.LogicalName, tipo = a.Type;
                var destino = tipo == "L"
                    ? $"{carpeta}\\{nombreBase}_log{(nLog++ == 0 ? "" : nLog.ToString())}.ldf"
                    : $"{carpeta}\\{nombreBase}{(nDatos++ == 0 ? "" : nDatos.ToString())}.mdf";
                mover.Add($"MOVE N'{logico.Replace("'", "''")}' TO N'{destino.Replace("'", "''")}'");
            }

            await cn.ExecuteAsync(
                $"RESTORE DATABASE [{nombreBase}] FROM DISK = @respaldo WITH {string.Join(", ", mover)}, RECOVERY",
                new { respaldo }, commandTimeout: 600);

            var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Central) { InitialCatalog = nombreBase };
            var cs = csb.ConnectionString;

            // La plantilla puede venir de una base real: la base nueva arranca sin datos de nadie.
            var aviso = await LimpiarBaseNuevaAsync(cs);
            return (cs, aviso);
        }
        catch (Exception ex)
        {
            return (null, "No se pudo crear la base: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Deja la base de una empresa nueva sin datos: borra comprobantes, cajas, movimientos,
    /// productos, clientes y los datos fiscales que venían en la plantilla, y vuelve los
    /// contadores internos a cero. No toca las tablas de configuración (Iva, FormaPago,
    /// TipoPrecio, Configuracion) ni las sucursales, que son el punto de partida.
    /// Devuelve null si salió bien, o el motivo si algo quedó sin limpiar.
    /// </summary>
    public static async Task<string> LimpiarBaseNuevaAsync(string cs)
    {
        // En orden: primero lo que depende de otras tablas.
        var tablas = new[]
        {
            "Detalle", "DetalleNotaCredito", "NotaCredito", "Factura",
            "DetallePresupuesto", "Presupuesto",
            "CajaIngreso", "EgresoCaja", "CajaTurno",
            "MovimientoStock", "ProductoCambioStock", "PagoQr",
            "StockSucursal", "Producto", "Cliente"
        };

        try
        {
            using var cn = await Db.AbrirAsync(cs);
            using var tx = cn.BeginTransaction();
            try
            {
                foreach (var t in tablas)
                    await cn.ExecuteAsync(
                        $"IF OBJECT_ID(N'dbo.{t}', N'U') IS NOT NULL DELETE FROM dbo.{t};",
                        transaction: tx, commandTimeout: 300);

                // Los datos fiscales de la plantilla no son los del cliente nuevo.
                foreach (var c in new[] { "Cuit", "IngresosBrutos", "Domicilio", "CP", "Telefono1", "Email" })
                    await cn.ExecuteAsync(
                        $@"IF COL_LENGTH('dbo.Empresa', '{c}') IS NOT NULL
                               EXEC sp_executesql N'UPDATE dbo.Empresa SET {c} = NULL';",
                        transaction: tx);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            // Los contadores internos arrancan de cero (el número de factura lo da ARCA).
            foreach (var t in new[] { "Factura", "Presupuesto", "NotaCredito" })
                await cn.ExecuteAsync(
                    $@"IF OBJECT_ID(N'dbo.{t}', N'U') IS NOT NULL
                          AND EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = OBJECT_ID(N'dbo.{t}'))
                           DBCC CHECKIDENT ('dbo.{t}', RESEED, 0) WITH NO_INFOMSGS;");

            return null;
        }
        catch (Exception ex)
        {
            return "La base se creó, pero no se pudo dejar sin datos: " + ex.GetBaseException().Message
                 + " · Corré sql/limpiar_base_para_entrega.sql sobre esa base antes de entregarla.";
        }
    }

    /// <summary>Crea (Id vacío) o actualiza una empresa. El código no se puede cambiar después del alta.</summary>
    public async Task<string> GuardarAsync(TenantConfig e)
    {
        bool nueva = e.Id == Guid.Empty;
        e.Codigo = e.Codigo?.Trim().ToLowerInvariant();
        e.Nombre = e.Nombre?.Trim();
        e.NombreFantasia = string.IsNullOrWhiteSpace(e.NombreFantasia) ? null : e.NombreFantasia.Trim();
        e.ConnectionString = NormalizarConexion(e.ConnectionString);
        e.Ambiente = string.IsNullOrWhiteSpace(e.Ambiente) ? "HOMO" : e.Ambiente.Trim().ToUpperInvariant();
        e.Moneda = string.IsNullOrWhiteSpace(e.Moneda) ? "PES" : e.Moneda.Trim().ToUpperInvariant();
        e.CertificadoPath = string.IsNullOrWhiteSpace(e.CertificadoPath) ? null : e.CertificadoPath.Trim();
        e.ClavePath = string.IsNullOrWhiteSpace(e.ClavePath) ? null : e.ClavePath.Trim();
        e.LeyendaTicket = (e.LeyendaTicket ?? "").Trim();
        if (e.LeyendaTicket.Length > 200) return "La leyenda del ticket no puede superar los 200 caracteres.";

        if (nueva && (e.Codigo == null || !CodigoValido.IsMatch(e.Codigo)))
            return "El código debe tener entre 2 y 30 caracteres: minúsculas, números o guiones.";
        if (nueva && e.Codigo == RolUsuario.CodigoCmTech) return "Ese código está reservado.";
        if (string.IsNullOrWhiteSpace(e.Nombre)) return "Falta la razón social de la empresa.";
        if (string.IsNullOrWhiteSpace(e.ConnectionString)) return "Falta la conexión a la base de datos.";
        if (e.PuntoVenta < 1 || e.PuntoVenta > 99999) return "El punto de venta debe estar entre 1 y 99999.";
        if (e.Ambiente != "HOMO" && e.Ambiente != "PROD") return "Ambiente inválido.";
        if (e.Concepto < 1 || e.Concepto > 3) return "Concepto inválido.";
        if (e.Moneda.Length != 3) return "La moneda debe tener 3 letras (ej. PES).";
        if (e.CertificadoPath == null || e.ClavePath == null) return "Faltan las rutas del certificado y la clave de ARCA.";

        using var cn = await Db.AbrirAsync(Central);
        if (nueva)
        {
            var existe = await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Empresa WHERE Codigo = @c", new { c = e.Codigo });
            if (existe > 0) return "Ya existe una empresa con ese código.";
            e.Id = Guid.NewGuid();
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.Empresa (Id, Codigo, Nombre, NombreFantasia, ConnectionString, PuntoVenta, Ambiente, CertificadoPath, ClavePath, Concepto, Moneda, LeyendaTicket, Activa)
                  VALUES (@Id, @Codigo, @Nombre, @NombreFantasia, @ConnectionString, @PuntoVenta, @Ambiente, @CertificadoPath, @ClavePath, @Concepto, @Moneda, @LeyendaTicket, 1)", e);
        }
        else
        {
            await cn.ExecuteAsync(
                @"UPDATE dbo.Empresa SET Nombre = @Nombre, NombreFantasia = @NombreFantasia, ConnectionString = @ConnectionString, PuntoVenta = @PuntoVenta,
                         Ambiente = @Ambiente, CertificadoPath = @CertificadoPath, ClavePath = @ClavePath,
                         Concepto = @Concepto, Moneda = @Moneda, LeyendaTicket = @LeyendaTicket
                  WHERE Id = @Id", e);
        }
        _tenants.Invalidar();
        return null;
    }

    public async Task<string> CambiarEstadoAsync(Guid id, bool activa, string motivo)
    {
        if (!activa && string.IsNullOrWhiteSpace(motivo)) return "Indicá el motivo de la suspensión.";
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync(
            "UPDATE dbo.Empresa SET Activa = @activa, MotivoSuspension = @motivo WHERE Id = @id",
            new { id, activa, motivo = activa ? null : motivo.Trim() });
        _tenants.Invalidar();
        return null;
    }

    /// <summary>
    /// Limpia lo que suele quedar al copiar una conexión de un archivo JSON o de un App.config:
    /// comillas alrededor, "connectionString=", barras invertidas dobles, espacios y saltos de línea.
    /// </summary>
    public static string NormalizarConexion(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return cs;
        cs = cs.Replace("\r", "").Replace("\n", " ").Trim();
        if (cs.StartsWith("connectionString=", StringComparison.OrdinalIgnoreCase)) cs = cs["connectionString=".Length..];
        cs = cs.Trim().TrimEnd('/', '>').Trim().Trim('"', '\'').Trim();
        cs = cs.Replace("\\\\", "\\");
        return cs;
    }

    /// <summary>Prueba la conexión y verifica que la base sea de APC y tenga la tabla CajaTurno.</summary>
    public async Task<(bool Ok, string Mensaje)> ProbarConexionAsync(string connectionString)
    {
        try
        {
            using var cn = new SqlConnection(NormalizarConexion(connectionString));
            await cn.OpenAsync();
            var emp = await cn.QueryFirstOrDefaultAsync<(string Nombre, string Cuit)>("SELECT TOP 1 Nombre, Cuit FROM dbo.Empresa");
            var tieneCaja = await cn.ExecuteScalarAsync<int>(
                "SELECT CASE WHEN OBJECT_ID(N'dbo.CajaTurno', N'U') IS NULL THEN 0 ELSE 1 END") == 1;

            if (emp.Nombre == null) return (false, "Conecta, pero la tabla Empresa está vacía: cargá los datos fiscales de la empresa.");
            var texto = $"Conexión correcta: {emp.Nombre} · CUIT {emp.Cuit}.";
            return tieneCaja
                ? (true, texto)
                : (false, texto + " Falta ejecutar el script 01_apcweb_cajaturno.sql en esta base.");
        }
        catch (Exception ex)
        {
            return (false, "No se pudo conectar: " + ex.GetBaseException().Message);
        }
    }
}

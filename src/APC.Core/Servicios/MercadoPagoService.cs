using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using APC.Core.Datos;
using APC.Core.Modelos;
using APC.Core.Tenants;
using Dapper;

namespace APC.Core.Servicios;

/// <summary>
/// Cobro con el QR de Mercado Pago (Orders API). El comercio pone su propia cuenta:
/// la plata va directo a su cuenta de MP, CM Tech no toca el dinero.
///
/// Circuito: se crea una order contra la caja (POS) de la sucursal, se muestra el QR,
/// el cliente paga con cualquier billetera y Mercado Pago avisa por webhook. Recién
/// cuando el pago está aprobado se emite la factura.
/// </summary>
public class MercadoPagoService
{
    private const string Api = "https://api.mercadopago.com";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly TenantRegistry _tenants;
    public MercadoPagoService(TenantRegistry tenants) => _tenants = tenants;

    private string Central => _tenants.Central;

    // ------------------------------------------------------------------ cuenta y cajas (base central)

    public async Task<MercadoPagoCuenta> CuentaAsync(Guid empresaId)
    {
        using var cn = await Db.AbrirAsync(Central);
        return await cn.QueryFirstOrDefaultAsync<MercadoPagoCuenta>(
            "SELECT * FROM dbo.MercadoPagoCuenta WHERE EmpresaId = @empresaId", new { empresaId });
    }

    public async Task<List<MercadoPagoCaja>> CajasAsync(Guid empresaId)
    {
        using var cn = await Db.AbrirAsync(Central);
        return (await cn.QueryAsync<MercadoPagoCaja>(
            "SELECT * FROM dbo.MercadoPagoCaja WHERE EmpresaId = @empresaId ORDER BY PuntoVenta",
            new { empresaId })).ToList();
    }

    /// <summary>Guarda el token y consulta /users/me para quedarse con el id de la cuenta.</summary>
    public async Task<string> GuardarCuentaAsync(Guid empresaId, string accessToken, string ambiente)
    {
        accessToken = (accessToken ?? "").Trim();
        if (string.IsNullOrWhiteSpace(accessToken)) return "Pegá el access token de la cuenta de Mercado Pago.";
        if (accessToken.Length > 400) return "El access token no parece válido: es demasiado largo.";

        var (ok, datos, error) = await GetAsync(accessToken, "/users/me");
        if (!ok) return "Mercado Pago no aceptó ese token: " + error;

        var userId = Texto(datos, "id");
        var apodo = Texto(datos, "nickname");

        try
        {
            using var cn = await Db.AbrirAsync(Central);
            await cn.ExecuteAsync(
                @"MERGE dbo.MercadoPagoCuenta AS d
                  USING (SELECT @empresaId AS EmpresaId) AS o ON d.EmpresaId = o.EmpresaId
                  WHEN MATCHED THEN UPDATE SET AccessToken = @accessToken, Ambiente = @ambiente,
                       UserId = @userId, Apodo = @apodo, Activo = 1
                  WHEN NOT MATCHED THEN INSERT (EmpresaId, AccessToken, Ambiente, UserId, Apodo)
                       VALUES (@empresaId, @accessToken, @ambiente, @userId, @apodo);",
                new { empresaId, accessToken, ambiente = ambiente == "PROD" ? "PROD" : "TEST", userId, apodo });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            return "No se pudo guardar. ¿Se ejecutó el script 21 en APCWeb_Central? Detalle: " + ex.Message;
        }
        return null;
    }

    public async Task<string> BorrarCuentaAsync(Guid empresaId)
    {
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync("DELETE FROM dbo.MercadoPagoCaja WHERE EmpresaId = @empresaId", new { empresaId });
        await cn.ExecuteAsync("DELETE FROM dbo.MercadoPagoCuenta WHERE EmpresaId = @empresaId", new { empresaId });
        return null;
    }

    /// <summary>
    /// Crea en Mercado Pago la tienda de la empresa y una caja por cada sucursal.
    /// Se puede repetir: lo que ya existe se reusa.
    /// </summary>
    public async Task<string> SincronizarCajasAsync(TenantConfig empresa, List<Sucursal> sucursales, DireccionTienda direccion = null)
    {
        var cuenta = await CuentaAsync(empresa.Id);
        if (cuenta == null) return "Primero guardá el access token de la cuenta.";
        if (string.IsNullOrWhiteSpace(cuenta.UserId)) return "Falta el id de la cuenta: volvé a guardar el token.";

        // ---- tienda ----
        if (string.IsNullOrWhiteSpace(cuenta.StoreId))
        {
            if (direccion == null || !direccion.Completa)
                return "Para crear la tienda hace falta la dirección del comercio: calle, localidad y provincia.";

            // Mercado Pago valida la localidad contra su catálogo: se manda escrita como figura ahí.
            var lugar = await NormalizarLugarAsync(direccion.Provincia, direccion.Localidad);
            if (lugar.Provincia != null) direccion.Provincia = lugar.Provincia;
            if (lugar.Localidad != null) direccion.Localidad = lugar.Localidad;

            // Mercado Pago exige latitud y longitud. Si no se cargaron a mano, se buscan por la dirección.
            if (!direccion.TieneCoordenadas)
            {
                var punto = await BuscarCoordenadasAsync(direccion);
                if (punto == null)
                    return "No se pudieron encontrar las coordenadas de esa dirección. "
                         + "Buscala en Google Maps, hacé clic derecho sobre el punto y pegá la latitud y la longitud en los campos de abajo.";
                direccion.Latitud = punto.Value.Lat;
                direccion.Longitud = punto.Value.Lon;
            }

            var nombre = Recortar(string.IsNullOrWhiteSpace(empresa.NombreFantasia) ? empresa.Nombre : empresa.NombreFantasia, 60);
            var (ok, datos, error) = await PostAsync(cuenta.AccessToken, $"/users/{cuenta.UserId}/stores", new
            {
                name = nombre,
                external_id = Clave(empresa.Codigo),
                location = new
                {
                    street_name = Recortar(SoloTexto(direccion.Calle), 60),
                    street_number = Recortar((direccion.Numero ?? "").Trim(), 10),
                    city_name = Recortar(SoloTexto(direccion.Localidad), 60),
                    state_name = Recortar(SoloTexto(direccion.Provincia), 60),
                    latitude = direccion.Latitud,
                    longitude = direccion.Longitud,
                    reference = Recortar(nombre, 60)
                }
            });
            if (!ok) return "No se pudo crear la tienda en Mercado Pago: " + error
                          + $" · Se mandó: {direccion.Calle} {direccion.Numero}, {direccion.Localidad}, {direccion.Provincia} "
                          + $"({direccion.Latitud.ToString(System.Globalization.CultureInfo.InvariantCulture)}, "
                          + $"{direccion.Longitud.ToString(System.Globalization.CultureInfo.InvariantCulture)})."
                          + (lugar.Aviso ?? "");

            cuenta.StoreId = Texto(datos, "id");
            using var cn = await Db.AbrirAsync(Central);
            await cn.ExecuteAsync("UPDATE dbo.MercadoPagoCuenta SET StoreId = @s WHERE EmpresaId = @e",
                new { s = cuenta.StoreId, e = empresa.Id });
        }

        // ---- una caja por sucursal ----
        var cajas = await CajasAsync(empresa.Id);
        foreach (var s in sucursales.Where(x => x.Activa))
        {
            var caja = cajas.FirstOrDefault(c => c.PuntoVenta == s.PuntoVenta);
            var externo = $"{Clave(empresa.Codigo)}POS{s.PuntoVenta:0000}";

            if (caja?.PosId != null)
            {
                await ActualizarCajaAsync(caja.Id, s.Id, caja.PosId, caja.QrEstatico);
                continue;
            }

            // Con store_id alcanza: mandar también external_store_id hace que Mercado Pago
            // valide los dos y falle con «External store id does not refer any store».
            var (ok, datos, error) = await PostAsync(cuenta.AccessToken, "/pos", new
            {
                name = Recortar(s.Nombre, 60),
                fixed_amount = true,
                store_id = cuenta.StoreId,
                external_id = externo,
                category = 621102
            });

            // Si ya existía con ese external_id, lo buscamos en vez de fallar.
            string posId = null, qr = null;
            if (ok)
            {
                posId = Texto(datos, "id");
                qr = TextoAnidado(datos, "qr", "image");
            }
            else
            {
                var (okBusca, lista, _) = await GetAsync(cuenta.AccessToken, $"/pos?external_id={Uri.EscapeDataString(externo)}");
                if (okBusca && lista.TryGetProperty("results", out var res) && res.GetArrayLength() > 0)
                {
                    posId = Texto(res[0], "id");
                    qr = TextoAnidado(res[0], "qr", "image");
                }
                if (posId == null) return $"No se pudo crear la caja de {s.Nombre}: {error}";
            }

            if (caja == null)
            {
                using var cn = await Db.AbrirAsync(Central);
                await cn.ExecuteAsync(
                    @"INSERT INTO dbo.MercadoPagoCaja (Id, EmpresaId, SucursalId, PuntoVenta, ExternalPosId, PosId, QrEstatico)
                      VALUES (NEWID(), @e, @suc, @pv, @ext, @pos, @qr)",
                    new { e = empresa.Id, suc = s.Id, pv = s.PuntoVenta, ext = externo, pos = posId, qr });
            }
            else
            {
                await ActualizarCajaAsync(caja.Id, s.Id, posId, qr);
            }
        }
        return null;
    }

    private async Task ActualizarCajaAsync(Guid id, Guid sucursalId, string posId, string qr)
    {
        using var cn = await Db.AbrirAsync(Central);
        await cn.ExecuteAsync(
            "UPDATE dbo.MercadoPagoCaja SET SucursalId = @suc, PosId = @pos, QrEstatico = @qr WHERE Id = @id",
            new { id, suc = sucursalId, pos = posId, qr });
    }

    // ------------------------------------------------------------------ cobro

    /// <summary>
    /// Crea la order con QR dinámico y la deja registrada en la base de la empresa.
    /// Devuelve el texto del QR para dibujarlo en pantalla.
    /// </summary>
    public async Task<(OrdenQr Orden, PagoQr Pago)> CobrarAsync(
        TenantConfig empresa, Sucursal sucursal, Guid usuarioId, decimal importe, string descripcion)
    {
        var pago = new PagoQr
        {
            Id = Guid.NewGuid(),
            Fecha = DateTime.Now,
            SucursalId = sucursal?.Id,
            UsuarioId = usuarioId,
            Importe = importe,
            Referencia = $"APC-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}".ToUpperInvariant()
        };

        var cuenta = await CuentaAsync(empresa.Id);
        if (cuenta == null || !cuenta.Listo)
            return (Falla("Esta empresa todavía no tiene configurada la cuenta de Mercado Pago."), pago);

        var puntoVenta = sucursal?.PuntoVenta ?? empresa.PuntoVenta;
        var cajas = await CajasAsync(empresa.Id);
        var caja = cajas.FirstOrDefault(c => c.PuntoVenta == puntoVenta);
        if (caja?.PosId == null)
            return (Falla($"No hay una caja de Mercado Pago para el punto de venta {puntoVenta:0000}. Pedile a CM Tech que la cree."), pago);

        var cuerpo = new
        {
            type = "qr",
            total_amount = importe.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            description = Recortar(descripcion, 60),
            external_reference = pago.Referencia,
            expiration_time = "PT10M",
            config = new { qr = new { external_pos_id = caja.ExternalPosId, mode = "dynamic" } },
            transactions = new
            {
                payments = new[]
                {
                    new { amount = importe.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) }
                }
            }
        };

        var (ok, datos, error) = await PostAsync(cuenta.AccessToken, "/v1/orders", cuerpo, idempotencia: pago.Referencia);
        if (!ok)
        {
            pago.Estado = EstadoPagoQr.Error;
            pago.Detalle = Recortar(error, 300);
            await GuardarPagoAsync(empresa.ConnectionString, pago);
            return (Falla("Mercado Pago no pudo generar el QR: " + error), pago);
        }

        pago.OrderId = Texto(datos, "id");
        pago.QrData = TextoAnidado(datos, "type_response", "qr_data") ?? Texto(datos, "qr_data");
        await GuardarPagoAsync(empresa.ConnectionString, pago);

        if (string.IsNullOrWhiteSpace(pago.QrData))
            return (Falla("Mercado Pago creó la order pero no devolvió el QR. Revisá que la caja esté en modo dinámico."), pago);

        return (new OrdenQr { Ok = true, OrderId = pago.OrderId, QrData = pago.QrData }, pago);
    }

    /// <summary>Pregunta a Mercado Pago cómo viene la order y actualiza el cobro.</summary>
    public async Task<PagoQr> RefrescarAsync(TenantConfig empresa, Guid pagoId)
    {
        var pago = await PagoAsync(empresa.ConnectionString, pagoId);
        if (pago == null || pago.Estado != EstadoPagoQr.Creada || string.IsNullOrWhiteSpace(pago.OrderId)) return pago;

        var cuenta = await CuentaAsync(empresa.Id);
        if (cuenta == null) return pago;

        var (ok, datos, _) = await GetAsync(cuenta.AccessToken, $"/v1/orders/{pago.OrderId}");
        if (!ok) return pago;

        var estado = (Texto(datos, "status") ?? "").ToLowerInvariant();
        var estadoDetalle = (Texto(datos, "status_detail") ?? "").ToLowerInvariant();

        if (estado == "processed" || estadoDetalle == "accredited")
        {
            pago.Estado = EstadoPagoQr.Pagada;
            pago.FechaPago = DateTime.Now;
            pago.PagoId = PrimerPagoId(datos);
        }
        else if (estado == "canceled")
        {
            pago.Estado = EstadoPagoQr.Cancelada;
        }
        else if (estado == "expired")
        {
            pago.Estado = EstadoPagoQr.Vencida;
        }

        if (pago.Estado != EstadoPagoQr.Creada) await ActualizarEstadoAsync(empresa.ConnectionString, pago);
        return pago;
    }

    public async Task<string> CancelarAsync(TenantConfig empresa, Guid pagoId)
    {
        var pago = await PagoAsync(empresa.ConnectionString, pagoId);
        if (pago == null) return "No se encontró el cobro.";
        if (pago.Estado == EstadoPagoQr.Pagada) return "El cobro ya está pagado: no se puede cancelar.";

        var cuenta = await CuentaAsync(empresa.Id);
        if (cuenta != null && !string.IsNullOrWhiteSpace(pago.OrderId))
            await PostAsync(cuenta.AccessToken, $"/v1/orders/{pago.OrderId}/cancel", new { });

        pago.Estado = EstadoPagoQr.Cancelada;
        await ActualizarEstadoAsync(empresa.ConnectionString, pago);
        return null;
    }

    /// <summary>
    /// Aviso de Mercado Pago (webhook). Sólo se toma el id del aviso: el estado real
    /// se consulta contra la API, así un aviso falso no puede marcar un cobro como pagado.
    /// </summary>
    public async Task<bool> AvisoAsync(TenantConfig empresa, string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return false;
        var pago = await PagoPorOrderAsync(empresa.ConnectionString, orderId.Trim());
        if (pago == null) return false;
        await RefrescarAsync(empresa, pago.Id);
        return true;
    }

    public async Task<PagoQr> PagoPorOrderAsync(string cs, string orderId)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.QueryFirstOrDefaultAsync<PagoQr>(
            "SELECT * FROM dbo.PagoQr WHERE OrderId = @orderId", new { orderId });
    }

    /// <summary>Marca el cobro con la factura que se emitió después de cobrarlo.</summary>
    public async Task AsociarFacturaAsync(string cs, Guid pagoId, Guid facturaId)
    {
        using var cn = await Db.AbrirAsync(cs);
        await cn.ExecuteAsync("UPDATE dbo.PagoQr SET FacturaId = @f WHERE Id = @id", new { f = facturaId, id = pagoId });
    }

    // ------------------------------------------------------------------ base de la empresa

    public async Task<PagoQr> PagoAsync(string cs, Guid id)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.QueryFirstOrDefaultAsync<PagoQr>("SELECT * FROM dbo.PagoQr WHERE Id = @id", new { id });
    }

    public async Task<PagoQr> PagoPorReferenciaAsync(string cs, string referencia)
    {
        using var cn = await Db.AbrirAsync(cs);
        return await cn.QueryFirstOrDefaultAsync<PagoQr>(
            "SELECT * FROM dbo.PagoQr WHERE Referencia = @referencia", new { referencia });
    }

    private static async Task GuardarPagoAsync(string cs, PagoQr p)
    {
        using var cn = await Db.AbrirAsync(cs);
        await cn.ExecuteAsync(
            @"INSERT INTO dbo.PagoQr (Id, Fecha, SucursalId, UsuarioId, Importe, Referencia, OrderId, Estado, Detalle, QrData)
              VALUES (@Id, @Fecha, @SucursalId, @UsuarioId, @Importe, @Referencia, @OrderId, @Estado, @Detalle, @QrData)", p);
    }

    private static async Task ActualizarEstadoAsync(string cs, PagoQr p)
    {
        using var cn = await Db.AbrirAsync(cs);
        await cn.ExecuteAsync(
            @"UPDATE dbo.PagoQr SET Estado = @Estado, PagoId = @PagoId, FechaPago = @FechaPago, Detalle = @Detalle
              WHERE Id = @Id", p);
    }

    // ------------------------------------------------------------------ http

    private static async Task<(bool Ok, JsonElement Datos, string Error)> GetAsync(string token, string ruta)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Get, Api + ruta);
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await EnviarAsync(pedido);
    }

    private static async Task<(bool Ok, JsonElement Datos, string Error)> PostAsync(
        string token, string ruta, object cuerpo, string idempotencia = null)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Post, Api + ruta)
        {
            Content = JsonContent.Create(cuerpo)
        };
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        pedido.Headers.Add("X-Idempotency-Key", idempotencia ?? Guid.NewGuid().ToString());
        return await EnviarAsync(pedido);
    }

    private static async Task<(bool Ok, JsonElement Datos, string Error)> EnviarAsync(HttpRequestMessage pedido)
    {
        try
        {
            using var respuesta = await Http.SendAsync(pedido);
            var texto = await respuesta.Content.ReadAsStringAsync();
            JsonElement datos = default;
            if (!string.IsNullOrWhiteSpace(texto))
            {
                try { datos = JsonSerializer.Deserialize<JsonElement>(texto, Json); } catch (JsonException) { }
            }
            if (respuesta.IsSuccessStatusCode) return (true, datos, null);

            var mensaje = Texto(datos, "message") ?? Texto(datos, "error") ?? respuesta.ReasonPhrase;
            return (false, datos, $"{(int)respuesta.StatusCode} {mensaje}");
        }
        catch (TaskCanceledException)
        {
            return (false, default, "Mercado Pago no respondió a tiempo.");
        }
        catch (HttpRequestException ex)
        {
            return (false, default, "No se pudo conectar con Mercado Pago: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ auxiliares

    private static OrdenQr Falla(string mensaje) => new() { Ok = false, Mensaje = mensaje };

    private static string Texto(JsonElement e, string campo)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(campo, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.ToString()
        };
    }

    private static string TextoAnidado(JsonElement e, string objeto, string campo)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(objeto, out var o)) return null;
        return Texto(o, campo);
    }

    private static string PrimerPagoId(JsonElement orden)
    {
        if (orden.ValueKind != JsonValueKind.Object) return null;
        if (!orden.TryGetProperty("transactions", out var t) || !t.TryGetProperty("payments", out var p)) return null;
        return p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0 ? Texto(p[0], "id") : null;
    }

    /// <summary>Solo letras y números en mayúscula: Mercado Pago no acepta cualquier cosa como external id.</summary>
    private static string Clave(string texto) =>
        new string((texto ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>
    /// Busca la latitud y la longitud de una dirección en OpenStreetMap (Nominatim, sin clave).
    /// Si no se puede, devuelve null y la dirección se carga a mano.
    /// </summary>
    private static async Task<(double Lat, double Lon)?> BuscarCoordenadasAsync(DireccionTienda d)
    {
        // Primero con la calle; si no aparece, alcanza con la localidad.
        var calle = string.Join(" ", new[] { d.Calle, d.Numero }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return await NominatimAsync(calle, d.Localidad, d.Provincia)
            ?? await NominatimAsync(null, d.Localidad, d.Provincia);
    }

    private static async Task<(double Lat, double Lon)?> NominatimAsync(string calle, string localidad, string provincia)
    {
        try
        {
            // Consulta estructurada: con texto libre, "Santa Rosa" puede caer en otra provincia.
            var partes = new List<string> { "format=json", "limit=1", "countrycodes=ar" };
            if (!string.IsNullOrWhiteSpace(calle)) partes.Add("street=" + Uri.EscapeDataString(calle));
            if (!string.IsNullOrWhiteSpace(localidad)) partes.Add("city=" + Uri.EscapeDataString(localidad));
            if (!string.IsNullOrWhiteSpace(provincia)) partes.Add("state=" + Uri.EscapeDataString(provincia));

            var json = await LeerJsonAsync("https://nominatim.openstreetmap.org/search?" + string.Join("&", partes));
            if (json == null) return null;
            using var doc = json;
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;

            var primero = doc.RootElement[0];
            if (!double.TryParse(Texto(primero, "lat"), System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out var lat)) return null;
            if (!double.TryParse(Texto(primero, "lon"), System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out var lon)) return null;
            return (lat, lon);
        }
        catch { return null; }
    }

    /// <summary>
    /// Deja la provincia y la localidad escritas como figuran en el catálogo de Mercado Libre,
    /// que es el que usa Mercado Pago para validar la dirección de la tienda.
    /// Si el catálogo no responde se manda lo que se escribió.
    /// </summary>
    /// <summary>
    /// Localidades que Mercado Pago acepta para una provincia, juntando sus dos catálogos.
    /// Si no se puede consultar, la lista vuelve vacía y la localidad se escribe a mano.
    /// </summary>
    public async Task<List<string>> LocalidadesAsync(string provincia)
    {
        var nombres = new List<string>();
        foreach (var baseUrl in new[] { "https://api.mercadolibre.com/classified_locations",
                                        "https://api.mercadolibre.com" })
        {
            try
            {
                var paises = await LeerJsonAsync($"{baseUrl}/countries/AR");
                if (paises == null) continue;
                using var doc = paises;
                if (!doc.RootElement.TryGetProperty("states", out var estados)) continue;

                var estado = Buscar(estados, provincia);
                if (estado == null) continue;

                var detalle = await LeerJsonAsync($"{baseUrl}/states/{estado.Value.Id}");
                if (detalle == null) continue;
                using var doc2 = detalle;
                if (!doc2.RootElement.TryGetProperty("cities", out var ciudades)) continue;

                nombres.AddRange(ciudades.EnumerateArray().Select(x => Texto(x, "name")).Where(x => x != null));
            }
            catch { }
        }
        return nombres
            .GroupBy(SinAcentos)
            .Select(g => g.First())
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<(string Provincia, string Localidad, string Aviso)> NormalizarLugarAsync(string provincia, string localidad)
    {
        // Mercado Libre tiene dos catálogos de localidades y no siempre traen los mismos pueblos:
        // se prueban los dos y, si ninguno la tiene, se manda la localidad tal como se escribió.
        foreach (var baseUrl in new[] { "https://api.mercadolibre.com/classified_locations",
                                        "https://api.mercadolibre.com" })
        {
            var (prov, loc) = await BuscarEnCatalogoAsync(baseUrl, provincia, localidad);
            if (loc != null) return (prov, loc, null);
        }
        return (null, null, " La localidad no figura en el catálogo de Mercado Pago: probá con la cabecera del departamento o con la ciudad más cercana.");
    }

    private static async Task<(string Provincia, string Localidad)> BuscarEnCatalogoAsync(string baseUrl, string provincia, string localidad)
    {
        try
        {
            var paises = await LeerJsonAsync($"{baseUrl}/countries/AR");
            if (paises == null) return (null, null);
            using var doc = paises;
            if (!doc.RootElement.TryGetProperty("states", out var estados)) return (null, null);

            var estado = Buscar(estados, provincia);
            if (estado == null) return (null, null);

            var detalle = await LeerJsonAsync($"{baseUrl}/states/{estado.Value.Id}");
            if (detalle == null) return (estado.Value.Nombre, null);
            using var doc2 = detalle;
            if (!doc2.RootElement.TryGetProperty("cities", out var ciudades)) return (estado.Value.Nombre, null);

            var ciudad = Buscar(ciudades, localidad);
            return (estado.Value.Nombre, ciudad?.Nombre);
        }
        catch { return (null, null); }
    }

    private static (string Id, string Nombre)? Buscar(JsonElement lista, string nombre)
    {
        var buscado = SinAcentos(nombre);
        if (buscado.Length == 0 || lista.ValueKind != JsonValueKind.Array) return null;

        foreach (var x in lista.EnumerateArray())
            if (SinAcentos(Texto(x, "name")) == buscado) return (Texto(x, "id"), Texto(x, "name"));

        // "Capital" vs "Córdoba Capital", "CABA" vs "Capital Federal", etc.
        foreach (var x in lista.EnumerateArray())
        {
            var n = SinAcentos(Texto(x, "name"));
            if (n.StartsWith(buscado) || buscado.StartsWith(n)) return (Texto(x, "id"), Texto(x, "name"));
        }
        return null;
    }

    private static string SinAcentos(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "";
        var plano = v.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var c in plano)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ");
    }

    /// <summary>GET a un servicio público (sin token) que devuelve JSON.</summary>
    private static async Task<JsonDocument> LeerJsonAsync(string url)
    {
        try
        {
            using var pedido = new HttpRequestMessage(HttpMethod.Get, url);
            pedido.Headers.UserAgent.ParseAdd("APCWeb/1.0 (soporte@cm-tech.com.ar)");
            using var respuesta = await Http.SendAsync(pedido);
            if (!respuesta.IsSuccessStatusCode) return null;
            var texto = await respuesta.Content.ReadAsStringAsync();
            return string.IsNullOrWhiteSpace(texto) ? null : JsonDocument.Parse(texto);
        }
        catch { return null; }
    }

    /// <summary>Mercado Pago rechaza números y signos en la localidad y la provincia.</summary>
    private static string SoloTexto(string v) =>
        string.IsNullOrWhiteSpace(v) ? v
        : System.Text.RegularExpressions.Regex.Replace(v, @"[^\p{L}\p{M}\s'.-]", "").Trim();

    private static string Recortar(string t, int largo) =>
        string.IsNullOrWhiteSpace(t) ? t : (t.Length <= largo ? t : t[..largo]);
}

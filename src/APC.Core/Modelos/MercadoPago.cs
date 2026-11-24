namespace APC.Core.Modelos;

/// <summary>Cuenta de Mercado Pago de una empresa (script 21, base central).</summary>
public class MercadoPagoCuenta
{
    public Guid EmpresaId { get; set; }
    public string AccessToken { get; set; }
    /// <summary>"TEST" o "PROD".</summary>
    public string Ambiente { get; set; } = "TEST";
    public string UserId { get; set; }
    public string StoreId { get; set; }
    public string Apodo { get; set; }
    public bool Activo { get; set; } = true;
    public DateTime FechaAlta { get; set; }

    public bool EsPrueba => !string.Equals(Ambiente?.Trim(), "PROD", StringComparison.OrdinalIgnoreCase);
    public bool Listo => Activo && !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(StoreId);
    /// <summary>Token enmascarado, para mostrarlo sin exponerlo entero.</summary>
    public string TokenCorto => string.IsNullOrWhiteSpace(AccessToken) ? ""
        : AccessToken.Length <= 12 ? "…" : AccessToken[..8] + "…" + AccessToken[^4..];
}

/// <summary>Caja (POS) de Mercado Pago: una por sucursal, es contra la que se crea cada QR.</summary>
public class MercadoPagoCaja
{
    public Guid Id { get; set; }
    public Guid EmpresaId { get; set; }
    public Guid? SucursalId { get; set; }
    public int PuntoVenta { get; set; }
    public string ExternalPosId { get; set; }
    public string PosId { get; set; }
    /// <summary>Imagen del QR impreso de la caja, como la devuelve Mercado Pago al crearla.</summary>
    public string QrEstatico { get; set; }
    /// <summary>
    /// true = el cliente escanea el QR pegado en el mostrador y la billetera le muestra el
    /// importe de la venta recién cargada (script 24). false = un QR nuevo en pantalla por venta.
    /// </summary>
    public bool UsaQrFijo { get; set; }
    public DateTime FechaAlta { get; set; }
    /// <summary>Número de caja dentro del punto de venta: 1, 2, 3… (script 26; las de antes son la 1).</summary>
    public int Numero { get; set; } = 1;
    /// <summary>Nombre que se le puso a la caja en Mercado Pago, por ejemplo «Centro · Caja 2».</summary>
    public string Nombre { get; set; }
    /// <summary>Cómo se muestra la caja en pantalla.</summary>
    public string Titulo => string.IsNullOrWhiteSpace(Nombre) ? $"Caja {Math.Max(Numero, 1)}" : Nombre;

    /// <summary>Se completa al listar, para mostrar el nombre junto al punto de venta.</summary>
    public string Sucursal { get; set; }
    public bool Creada => !string.IsNullOrWhiteSpace(PosId);
}

/// <summary>
/// Dirección de la tienda en Mercado Pago. No se guarda: se usa una sola vez, al crearla.
/// Mercado Pago valida que la provincia exista y que concomitancia con la localidad, así que no admite relleno.
/// </summary>
public class DireccionTienda
{
    public string Calle { get; set; }
    public string Numero { get; set; }
    public string Localidad { get; set; }
    public string Provincia { get; set; }
    /// <summary>Mercado Pago exige las coordenadas. Si quedan en cero se buscan por la dirección.</summary>
    public double Latitud { get; set; }
    public double Longitud { get; set; }

    public bool TieneCoordenadas => Latitud != 0 && Longitud != 0;

    /// <summary>La dirección en una línea, para buscar las coordenadas.</summary>
    public string Linea => string.Join(", ", new[]
    {
        string.Join(" ", new[] { Calle, Numero }.Where(x => !string.IsNullOrWhiteSpace(x))),
        Localidad, Provincia, "Argentina"
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    public bool Completa => !string.IsNullOrWhiteSpace(Calle)
                         && !string.IsNullOrWhiteSpace(Localidad)
                         && !string.IsNullOrWhiteSpace(Provincia);

    /// <summary>Jurisdicciones tal como las espera Mercado Pago.</summary>
    public static readonly string[] Provincias =
    {
        "Buenos Aires", "Catamarca", "Chaco", "Chubut", "Ciudad Autónoma de Buenos Aires",
        "Córdoba", "Corrientes", "Entre Ríos", "Formosa", "Jujuy", "La Pampa", "La Rioja",
        "Mendoza", "Misiones", "Neuquén", "Río Negro", "Salta", "San Juan", "San Luis",
        "Santa Cruz", "Santa Fe", "Santiago del Estero", "Tierra del Fuego", "Tucumán"
    };

    /// <summary>Parte un domicilio como «Av. Colón 1234» en calle y número.</summary>
    public static (string Calle, string Numero) Partir(string domicilio)
    {
        var texto = (domicilio ?? "").Trim();
        if (texto.Length == 0) return ("", "");
        var m = System.Text.RegularExpressions.Regex.Match(texto, @"^(.*?)[\s,]*(\d+[A-Za-z]?)$");
        return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value) : (texto, "");
    }
}

/// <summary>Un cobro con QR (script 22, base de la empresa).</summary>
public class PagoQr
{
    public Guid Id { get; set; }
    public DateTime Fecha { get; set; }
    public Guid? SucursalId { get; set; }
    public Guid? UsuarioId { get; set; }
    public decimal Importe { get; set; }
    public string Referencia { get; set; }
    public string OrderId { get; set; }
    public string PagoId { get; set; }
    public string Estado { get; set; } = EstadoPagoQr.Creada;
    public string Detalle { get; set; }
    public string QrData { get; set; }
    public Guid? FacturaId { get; set; }
    public DateTime? FechaPago { get; set; }

    public bool Pagado => Estado == EstadoPagoQr.Pagada;

    /// <summary>
    /// Los QR interoperables (los que puede pagar MODO o la app del banco) son EMVCo y
    /// arrancan con «0002». Si Mercado Pago devuelve un link, sólo lo lee su propia app.
    /// </summary>
    public bool Interoperable => QrData != null && QrData.StartsWith("0002");
}

public static class EstadoPagoQr
{
    public const string Creada = "CREADA";
    public const string Pagada = "PAGADA";
    public const string Cancelada = "CANCELADA";
    public const string Vencida = "VENCIDA";
    public const string Error = "ERROR";
}

/// <summary>Lo que devuelve Mercado Pago al crear la order del QR.</summary>
public class OrdenQr
{
    public bool Ok { get; set; }
    public string OrderId { get; set; }
    /// <summary>El texto que se convierte en el código QR.</summary>
    public string QrData { get; set; }
    public string Mensaje { get; set; }
    /// <summary>La venta va contra el QR impreso de la caja: no hay QR nuevo que mostrar.</summary>
    public bool Fijo { get; set; }
    /// <summary>Imagen del QR impreso, para mostrarla igual en pantalla si el cliente no lo ve.</summary>
    public string QrImagen { get; set; }
}

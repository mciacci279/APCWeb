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
    public string QrEstatico { get; set; }
    public DateTime FechaAlta { get; set; }

    /// <summary>Se completa al listar, para mostrar el nombre junto al punto de venta.</summary>
    public string Sucursal { get; set; }
    public bool Creada => !string.IsNullOrWhiteSpace(PosId);
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
}

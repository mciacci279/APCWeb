namespace APC.Core.Modelos;

public static class RolUsuario
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    /// <summary>Supervisor de sucursal: ve y cierra las cajas de su sucursal.</summary>
    public const string Supervisor = "Supervisor";
    public const string Empleado = "Empleado";

    public static string Titulo(string rol) => rol switch
    {
        SuperAdmin => "CM Tech",
        Admin => "Administrador",
        Supervisor => "Supervisor",
        _ => "Cajero"
    };
    /// <summary>Código de empresa reservado para ingresar al panel de CM Tech.</summary>
    public const string CodigoCmTech = "cmtech";
}

/// <summary>Usuario de la base central, tal como se lee al validar el ingreso.</summary>
public class UsuarioSesion
{
    public Guid Id { get; set; }
    public string NombreUsuario { get; set; }
    public string Nombre { get; set; }
    public string Apellido { get; set; }
    public Guid? EmpresaId { get; set; }
    public string Rol { get; set; }
    public bool Activo { get; set; }
    public string ClaveHash { get; set; }
    public int IntentosFallidos { get; set; }
    public DateTime? BloqueadoHasta { get; set; }
    public string Email { get; set; }
    /// <summary>Doble factor: secreto de la app autenticadora y si está activado.</summary>
    public string TotpSecreto { get; set; }
    public bool TotpActivo { get; set; }
    public string NombreCompleto => string.Join(" ", new[] { Nombre, Apellido }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public class UsuarioListado
{
    public Guid Id { get; set; }
    public string NombreUsuario { get; set; }
    public string Nombre { get; set; }
    public string Apellido { get; set; }
    public string Email { get; set; }
    public string Rol { get; set; }
    public bool Activo { get; set; }
    public DateTime? UltimoIngreso { get; set; }
    public DateTime? BloqueadoHasta { get; set; }
    /// <summary>Sucursal en la que trabaja. Vacío = la casa central.</summary>
    public Guid? SucursalId { get; set; }
    public bool EsAdmin => Rol == RolUsuario.Admin;
    public bool EsSupervisor => Rol == RolUsuario.Supervisor;
    public string RolTitulo => RolUsuario.Titulo(Rol);
    public bool Bloqueado => BloqueadoHasta > DateTime.Now;
}

public class EmpresaDatos
{
    public Guid Id { get; set; }
    public string Nombre { get; set; }
    public string Descripcion { get; set; }
    public string Cuit { get; set; }
    public string IngresosBrutos { get; set; }
    public DateTime? FechaInicioActividades { get; set; }
    public string Domicilio { get; set; }
    public string Telefono1 { get; set; }
    public string CP { get; set; }
    public string Email { get; set; }
    public string CondicionIva { get; set; }
    /// <summary>Fila de dbo.Iva que define la condición frente al IVA del emisor.</summary>
    public Guid? IvaId { get; set; }
    public bool EsMonotributo { get; set; }
    /// <summary>Responsable inscripto: el único que lleva Libro IVA Ventas con IVA discriminado.</summary>
    public bool EsResponsableInscripto { get; set; }
    public long CuitNumero => long.TryParse((Cuit ?? "").Replace("-", "").Trim(), out var c) ? c : 0;
}

/// <summary>Una condición frente al IVA del emisor, como está en dbo.Iva.</summary>
public class CondicionIvaItem
{
    public Guid Id { get; set; }
    public string Nombre { get; set; }
    public string LetraComprobante { get; set; }
}

public class ProductoItem
{
    public Guid Id { get; set; }
    public string Codigo { get; set; }
    public string Nombre { get; set; }
    public decimal PrecioVenta1 { get; set; }
    public decimal Stock { get; set; }
    public bool IvaDiferenciado { get; set; }
    public bool IvaExento { get; set; }
    public bool OmitirControlStock { get; set; }
}

public class FormaPagoItem
{
    public Guid Id { get; set; }
    public string Nombre { get; set; }
    public decimal PorcentajeRecargo { get; set; }
    public decimal PorcentajeDescuento { get; set; }
    public bool EsCtaCte { get; set; }
    /// <summary>Lista de precios de la forma de pago (columna FormaPago.Precio de APC): 1, 2 o 3.</summary>
    public int ListaPrecio { get; set; } = 1;
    /// <summary>Tipo de precio asociado (script 18). Null = precio de lista, sin ajuste.</summary>
    public Guid? TipoPrecioId { get; set; }
    public string TipoPrecio { get; set; }
    /// <summary>Cobra con el QR de Mercado Pago (script 22).</summary>
    public bool EsMercadoPago { get; set; }
    /// <summary>Cobra con el QR interoperable del propio comercio (script 23).</summary>
    public bool EsQrPropio { get; set; }
    public bool EsEfectivo => EsNombreEfectivo(Nombre);

    /// <summary>Formas de pago que cuentan como efectivo en caja (vuelto y arqueo).</summary>
    public static bool EsNombreEfectivo(string nombre) =>
        nombre != null && (nombre.Contains("efectivo", StringComparison.OrdinalIgnoreCase)
                        || nombre.Contains("contado", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Condición frente al IVA del receptor (coincide con los flags de la tabla Iva).</summary>
public enum CondicionReceptor
{
    ConsumidorFinal,
    ResponsableInscripto,
    Monotributo,
    Exento,
    NoResponsable
}

public class RenglonCarrito
{
    /// <summary>Identificador del renglón en pantalla (para enfocar sus campos).</summary>
    public string Clave { get; } = Guid.NewGuid().ToString("N");
    public ProductoItem Producto { get; set; }
    /// <summary>Producto sin precio cargado (ej. "01 varios"): el vendedor escribe el precio.</summary>
    public bool PrecioLibre => Producto != null && Producto.PrecioVenta1 <= 0;
    public decimal Cantidad { get; set; } = 1;
    public decimal PrecioUnitario { get; set; }
    /// <summary>Descuento propio del renglón (%). Excluyente con RecargoPorc.</summary>
    public decimal DescuentoPorc { get; set; }
    /// <summary>Incremento propio del renglón (%). Excluyente con DescuentoPorc.</summary>
    public decimal RecargoPorc { get; set; }
}

public class NuevaFactura
{
    public CondicionReceptor Condicion { get; set; } = CondicionReceptor.ConsumidorFinal;
    public Guid? ClienteId { get; set; }
    public string ClienteNombre { get; set; } = "Consumidor final";
    public string ClienteDocumento { get; set; }
    public string ClienteDomicilio { get; set; }
    public string ClienteLocalidad { get; set; }
    public Guid FormaPagoId { get; set; }
    public string Observaciones { get; set; }
    /// <summary>Descuento sobre el total (%), como nupDescuentoTotal de APC. La forma de pago lo propone.</summary>
    public decimal DescuentoTotalPorc { get; set; }
    /// <summary>Recargo sobre el total (%), como nupRecargo de APC. La forma de pago lo propone.</summary>
    public decimal RecargoTotalPorc { get; set; }
    public List<RenglonCarrito> Renglones { get; set; } = new();
}

public class ResultadoEmision
{
    public bool Ok { get; set; }
    /// <summary>La factura quedó guardada aunque ARCA haya fallado (se puede reintentar).</summary>
    public Guid? FacturaId { get; set; }
    public string Letra { get; set; }
    public int PuntoVenta { get; set; }
    public long Numero { get; set; }
    public string Cae { get; set; }
    public string Mensaje { get; set; }
    /// <summary>Comprobante no fiscal: Numero es el número interno.</summary>
    public bool EsNotaPedido { get; set; }
    /// <summary>Presupuesto guardado: FacturaId es el Id del presupuesto.</summary>
    public bool EsPresupuesto { get; set; }
}

public class TotalesFactura
{
    public decimal Total { get; set; }
    public decimal Iva { get; set; }
    public decimal NoGravado { get; set; }
    public decimal MontoDescuento { get; set; }
    public decimal MontoRecargo { get; set; }
    public decimal Neto => Total - Iva - NoGravado;
}

public class ComprobanteImpresion
{
    public Guid Id { get; set; }
    public DateTime Fecha { get; set; }
    public string Tipo { get; set; }
    public int Terminal { get; set; }
    public int? Numero { get; set; }
    public int? TipoComprobanteAFIP { get; set; }
    public decimal MontoTotal { get; set; }
    public decimal MontoIva { get; set; }
    public decimal? MontoNoGravado { get; set; }
    public string ClienteNombre { get; set; }
    public string ClienteCUIT { get; set; }
    public string ClienteDomicilio { get; set; }
    public string CAE { get; set; }
    public string CAEVencimiento { get; set; }
    public string QRURLAFIP { get; set; }
    public string FormaPago { get; set; }
    public string Usuario { get; set; }
    public bool? Anulada { get; set; }
    public int? NumeroInterno { get; set; }
    public string Observaciones { get; set; }
    public string ClienteLocalidad { get; set; }
    public decimal? MontoIvaTipoAfip4 { get; set; }
    public decimal? MontoIvaTipoAfip5 { get; set; }
    public decimal? MontoIvaTipoAfip6 { get; set; }
    public string ClienteCondicionIva { get; set; }
    public bool ClienteEsConsumidorFinal { get; set; }
    public List<RenglonImpresion> Renglones { get; set; } = new();

    public bool EsNotaCredito { get; set; }
    public bool Impresa { get; set; }
    /// <summary>Venta no fiscal (APC: Factura impresa sin CAE).</summary>
    public bool EsNotaPedido => !EsNotaCredito && Impresa && string.IsNullOrEmpty(CAE);
    /// <summary>Letra a mostrar: X en la nota de pedido.</summary>
    public string Letra => EsNotaPedido ? "X" : Tipo;
    public string TipoFacturaRelacionada { get; set; }
    public int? TerminalFacturaRelacionada { get; set; }
    public int? NroFacturaRelacionada { get; set; }

    /// <summary>Nombre del comprobante según el código de ARCA.</summary>
    public string NombreComprobante => EsNotaPedido ? "NOTA DE PEDIDO" : TipoComprobanteAFIP switch
    {
        1 => "FACTURA A",
        6 => "FACTURA B",
        11 => "FACTURA C",
        3 => "NOTA DE CRÉDITO A",
        8 => "NOTA DE CRÉDITO B",
        13 => "NOTA DE CRÉDITO C",
        _ => (EsNotaCredito ? "NOTA DE CRÉDITO " : "FACTURA ") + Tipo
    };

    /// <summary>"Factura B 00001-00000002" para la nota de crédito; null en una factura.</summary>
    public string ComprobanteAsociado => EsNotaCredito && NroFacturaRelacionada != null
        ? $"Factura {TipoFacturaRelacionada} {(TerminalFacturaRelacionada ?? 0):00000}-{(NroFacturaRelacionada ?? 0):00000000}"
        : null;

    /// <summary>Punto de venta y número en el formato de ARCA: 00001-00000002.</summary>
    public string NumeroCompleto => EsNotaPedido
        ? $"{(NumeroInterno ?? 0):00000000}"
        : $"{Terminal:00000}-{(Numero ?? 0):00000000}";
}

public class RenglonImpresion
{
    public string Producto { get; set; }
    public string Codigo { get; set; }
    public decimal CantidadRenglon { get; set; }
    public decimal PU { get; set; }
    public decimal MontoRenglon { get; set; }
    public decimal? IvaRenglon { get; set; }
    public decimal MontoIvaRenglon { get; set; }
    public decimal DescuentoRenglon { get; set; }
    public decimal MontoDescuentoRenglon { get; set; }
}

public class ComprobanteListado
{
    public Guid Id { get; set; }
    public DateTime Fecha { get; set; }
    public string Tipo { get; set; }
    public int Terminal { get; set; }
    public int? Numero { get; set; }
    public string ClienteNombre { get; set; }
    public decimal MontoTotal { get; set; }
    public string CAE { get; set; }
    public string FormaPago { get; set; }
    // Nota de crédito que anula la factura (si existe)
    public Guid? NcId { get; set; }
    public string NcTipo { get; set; }
    public int? NcTerminal { get; set; }
    public int? NcNumero { get; set; }
    public string NcCAE { get; set; }
    public int? NumeroInterno { get; set; }
    public bool Impresa { get; set; }
    public bool MarcadaAnulada { get; set; }
    public bool EsNotaPedido => Impresa && string.IsNullOrEmpty(CAE);
    public bool Anulada => MarcadaAnulada || (NcId != null && !string.IsNullOrEmpty(NcCAE));
}

public class CajaTurno
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    /// <summary>Sucursal donde se abrió la caja.</summary>
    public Guid? SucursalId { get; set; }
    public DateTime FechaApertura { get; set; }
    public decimal MontoInicial { get; set; }
    public DateTime? FechaCierre { get; set; }
    public decimal? EfectivoEsperado { get; set; }
    public decimal? EfectivoContado { get; set; }
    public decimal? Diferencia { get; set; }
    public string Observaciones { get; set; }

    /// <summary>Se completan al listar las cajas de la sucursal.</summary>
    public string Usuario { get; set; }
    public string Sucursal { get; set; }
    public decimal Ventas { get; set; }
    public bool Abierta => FechaCierre == null;

    /// <summary>
    /// Caja de la sucursal (PuestoCaja). Con el script 28, si está cargada en la tabla el turno es de la caja
    /// (no del usuario): se abre y cierra por caja y suma lo que hizo cualquier cajero mientras estuvo en ella.
    /// </summary>
    public Guid? PuestoCajaId { get; set; }
    /// <summary>Quién cerró la caja (script 28).</summary>
    public Guid? UsuarioCierreId { get; set; }
    public string UsuarioCierre { get; set; }
    /// <summary>El turno es de una caja de la sucursal (tiene PuestoCajaId guardado), no de un usuario.</summary>
    public bool EsDeCaja { get; set; }
    public string Puesto { get; set; }
    /// <summary>Fila armada desde un ingreso a una caja que todavía sigue en uso.</summary>
    public bool EnUso { get; set; }
}

public class ArqueoFormaPago
{
    public string FormaPago { get; set; }
    public int Comprobantes { get; set; }
    public decimal Total { get; set; }
}

/// <summary>Un tipo de movimiento del turno (facturas, notas de pedido, notas de crédito, caja) por forma de pago.</summary>
public class ArqueoGrupo
{
    public string Codigo { get; set; }   // F, S, P, N, C
    public string Titulo { get; set; }
    public List<ArqueoFormaPago> Formas { get; set; } = new();
    public int Comprobantes => Formas.Sum(f => f.Comprobantes);
    public decimal Total => Formas.Sum(f => f.Total);
}

public class ArqueoResumen
{
    public CajaTurno Turno { get; set; }
    public List<ArqueoGrupo> Grupos { get; set; } = new();
    public decimal VentasFiscales { get; set; }
    public int CantidadFiscales { get; set; }
    public decimal VentasNoFiscales { get; set; }
    public int CantidadNoFiscales { get; set; }
    public decimal NotasCredito { get; set; }
    public decimal IngresosEgresos { get; set; }
    /// <summary>Total vendido neto: facturas + notas de pedido − notas de crédito.</summary>
    public decimal Ventas => VentasFiscales + VentasNoFiscales + NotasCredito;
    public int CantidadComprobantes => CantidadFiscales + CantidadNoFiscales;
    public decimal EfectivoEsperado { get; set; }
}

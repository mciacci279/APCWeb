namespace APC.Core.Modelos;

/// <summary>
/// Una caja de una sucursal (Caja 1, Caja 2…), tabla dbo.PuestoCaja (script 27).
/// Si la sucursal tiene más de una, cada usuario elige al ingresar en cuál trabaja.
/// </summary>
public class PuestoCaja
{
    public Guid Id { get; set; }
    public Guid SucursalId { get; set; }
    public int Numero { get; set; }
    public string Nombre { get; set; }
    /// <summary>Caja de Mercado Pago con la que cobra (MercadoPagoCaja.Id en la base central).</summary>
    public Guid? MercadoPagoCajaId { get; set; }
    public bool Activa { get; set; } = true;
    public DateTime FechaAlta { get; set; }

    // ---- quién la está usando ahora (se completa al listar) ----
    public Guid? SesionId { get; set; }
    public Guid? UsuarioId { get; set; }
    public string Usuario { get; set; }
    public DateTime? Desde { get; set; }
    public DateTime? UltimoLatido { get; set; }

    public bool Ocupada => SesionId != null;
    public string Titulo => string.IsNullOrWhiteSpace(Nombre) ? $"Caja {Numero}" : Nombre;
}

/// <summary>Un período de un usuario en una caja, tabla dbo.PuestoCajaSesion (script 27).</summary>
public class PuestoCajaSesion
{
    public Guid Id { get; set; }
    public Guid CajaId { get; set; }
    public Guid UsuarioId { get; set; }
    public DateTime FechaIngreso { get; set; }
    public DateTime UltimoLatido { get; set; }
    public DateTime? FechaSalida { get; set; }
    /// <summary>SALIO, INACTIVIDAD, LIBERADA o CAMBIO. Null mientras sigue abierta.</summary>
    public string Motivo { get; set; }
    public Guid? LiberadaPor { get; set; }

    // ---- para mostrar (se completan al listar) ----
    public string Caja { get; set; }
    public int CajaNumero { get; set; }
    public Guid SucursalId { get; set; }
    public string Usuario { get; set; }
    public string LiberadaPorUsuario { get; set; }
    public Guid? MercadoPagoCajaId { get; set; }

    public bool Abierta => FechaSalida == null;
    public string TituloCaja => string.IsNullOrWhiteSpace(Caja) ? $"Caja {CajaNumero}" : Caja;
    /// <summary>Hasta la salida o, si sigue abierta, hasta el último aviso de la pantalla.</summary>
    public TimeSpan Duracion => (FechaSalida ?? UltimoLatido) - FechaIngreso;
}

public static class MotivoSalidaCaja
{
    public const string Salio = "SALIO";
    public const string Inactividad = "INACTIVIDAD";
    public const string Liberada = "LIBERADA";
    public const string Cambio = "CAMBIO";
}

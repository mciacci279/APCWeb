namespace APC.Core.Modelos;

public static class EstadoTicket
{
    /// <summary>Ideas a futuro: no son trabajo tomado, esperan ahí hasta que se decidan.</summary>
    public const string Idea = "IDEA";
    public const string Pendiente = "PENDIENTE";
    public const string EnCurso = "EN_CURSO";
    public const string Esperando = "ESPERANDO";
    public const string Listo = "LISTO";

    /// <summary>Las columnas del tablero, en orden.</summary>
    public static readonly (string Clave, string Titulo)[] Columnas =
    {
        (Idea, "Ideas"),
        (Pendiente, "Pendiente"),
        (EnCurso, "En curso"),
        (Esperando, "Esperando al cliente"),
        (Listo, "Listo")
    };

    public static string Titulo(string estado) =>
        Columnas.FirstOrDefault(c => c.Clave == estado).Titulo ?? estado;
}

public static class PrioridadTicket
{
    public const string Baja = "BAJA";
    public const string Normal = "NORMAL";
    public const string Alta = "ALTA";
    public static readonly string[] Todas = { Baja, Normal, Alta };
}

public static class TipoTicket
{
    public const string Error = "ERROR";
    public const string Pedido = "PEDIDO";
    public const string Mejora = "MEJORA";
    public const string Tarea = "TAREA";
    public static readonly string[] Todos = { Error, Pedido, Mejora, Tarea };

    public static string Titulo(string tipo) => tipo switch
    {
        Error => "Error",
        Pedido => "Pedido del cliente",
        Mejora => "Mejora",
        _ => "Tarea"
    };
}

/// <summary>Una tarjeta del tablero de trabajo de CM Tech.</summary>
public class Ticket
{
    public Guid Id { get; set; }
    public int Numero { get; set; }
    public string Titulo { get; set; }
    public string Detalle { get; set; }
    public string Estado { get; set; } = EstadoTicket.Pendiente;
    public string Prioridad { get; set; } = PrioridadTicket.Normal;
    public string Tipo { get; set; } = TipoTicket.Pedido;
    public Guid? EmpresaId { get; set; }
    public Guid? AsignadoA { get; set; }
    public DateTime? Vencimiento { get; set; }
    public int Orden { get; set; }
    public DateTime FechaAlta { get; set; }
    public DateTime? FechaCierre { get; set; }

    // Datos que se leen junto con el ticket
    public string Empresa { get; set; }
    public string Asignado { get; set; }
    public string CreadoPorNombre { get; set; }
    public int Notas { get; set; }

    public bool EsNuevo => Id == Guid.Empty;
    public bool Cerrado => Estado == EstadoTicket.Listo;
    public bool Vencido => Vencimiento != null && !Cerrado && Vencimiento.Value.Date < DateTime.Today;
    public bool VenceHoy => Vencimiento?.Date == DateTime.Today && !Cerrado;

    /// <summary>Texto que se muestra en la tarjeta. La prioridad normal no se escribe, para no ensuciar.</summary>
    public string TextoPrioridad => Prioridad switch
    {
        PrioridadTicket.Alta => "Prioridad alta",
        PrioridadTicket.Baja => "Baja",
        _ => null
    };

    public string ClasePrioridad => Prioridad switch
    {
        PrioridadTicket.Alta => "prio-alta",
        PrioridadTicket.Baja => "prio-baja",
        _ => "prio-normal"
    };
}

public class TicketNota
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public DateTime Fecha { get; set; }
    public string Texto { get; set; }
    public string Usuario { get; set; }
}

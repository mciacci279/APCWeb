namespace APC.Core.Modelos;

/// <summary>Un script de la carpeta sql.</summary>
public class ScriptSql
{
    public string Nombre { get; set; }
    public int Numero { get; set; }
    public string Titulo { get; set; }
    public string Ruta { get; set; }
    /// <summary>true = va en APCWeb_Central; false = en la base de cada empresa.</summary>
    public bool ParaCentral { get; set; }
}

/// <summary>Fila de dbo.ApcWebScript.</summary>
public class ScriptCorrido
{
    public string Nombre { get; set; }
    public DateTime Fecha { get; set; }
    public string Quien { get; set; }
    public string Resultado { get; set; }
}

/// <summary>Un script visto desde una base: corrido o pendiente.</summary>
public class ScriptEnBase
{
    public ScriptSql Script { get; set; }
    public DateTime? Fecha { get; set; }
    public string Quien { get; set; }
    public string Resultado { get; set; }

    public bool Corrido => Fecha != null;
}

/// <summary>Estado de una base: la central, la plantilla o la de una empresa.</summary>
public class EstadoBase
{
    public Guid? EmpresaId { get; set; }
    public string Codigo { get; set; }
    public string Nombre { get; set; }
    public string Base { get; set; }
    public string ConnectionString { get; set; }
    public bool EsCentral { get; set; }
    public bool EsPlantilla { get; set; }
    public bool Accesible { get; set; }
    public string Error { get; set; }
    public List<ScriptEnBase> Scripts { get; set; } = new();

    public int Pendientes => Scripts.Count(x => !x.Corrido);
    public int Total => Scripts.Count;
    public bool AlDia => Accesible && Pendientes == 0;
    public ScriptEnBase Ultimo => Scripts.Where(x => x.Corrido).OrderByDescending(x => x.Script.Numero).FirstOrDefault();
}

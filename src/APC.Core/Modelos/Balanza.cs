namespace APC.Core.Modelos;

/// <summary>
/// Cómo lee esta empresa los códigos que imprime la balanza Systel (script 25).
/// Son los mismos tres parámetros que el sistema de escritorio tiene en su app.config.
/// </summary>
public class ConfigBalanza
{
    public bool Activa { get; set; }
    /// <summary>Q = el código trae la cantidad; P = trae el importe ya calculado por la balanza.</summary>
    public string Modo { get; set; } = "Q";
    /// <summary>El importe viene con dos decimales (5200 = $52,00) en vez de entero.</summary>
    public bool Decimales { get; set; }
    /// <summary>Producto comodín al que se le carga el total cuando se lee un código 22.</summary>
    public string CodigoSuma { get; set; } = "1234567890";

    public bool PorCantidad => !string.Equals(Modo?.Trim(), "P", StringComparison.OrdinalIgnoreCase);
}

public static class TipoCodigoBalanza
{
    /// <summary>Venta por peso: el campo de cantidad viene en gramos.</summary>
    public const string Peso = "20";
    /// <summary>Venta por unidad.</summary>
    public const string Unidad = "21";
    /// <summary>Total del ticket: la suma de los renglones.</summary>
    public const string Total = "22";
}

/// <summary>Lo que dice un código de balanza, ya desarmado.</summary>
public class LecturaBalanza
{
    /// <summary>false = no es un código de balanza, hay que buscarlo como siempre.</summary>
    public bool EsDeBalanza { get; set; }
    public string Tipo { get; set; }
    /// <summary>PLU del producto, sin los ceros de la izquierda. Vacío en los códigos 22.</summary>
    public string Plu { get; set; }
    public decimal Cantidad { get; set; } = 1;
    /// <summary>Precio que impone la balanza. Null = se usa el precio del producto.</summary>
    public decimal? Precio { get; set; }

    public bool EsTotal => Tipo == TipoCodigoBalanza.Total;
    public bool EsPeso => Tipo == TipoCodigoBalanza.Peso;
}

/// <summary>Un código de balanza ya resuelto contra el catálogo, listo para cargar.</summary>
public class RenglonBalanza
{
    public ProductoItem Producto { get; set; }
    public decimal Cantidad { get; set; }
    /// <summary>Precio unitario a usar. Null = el del producto.</summary>
    public decimal? Precio { get; set; }
    public bool EsTotal { get; set; }
    public string Error { get; set; }

    public bool Ok => Error == null && Producto != null;
}

namespace APC.Core.Modelos;

/// <summary>
/// Tipo de precio: un nombre y un ajuste en % sobre el precio de lista (script 18).
/// Positivo recarga (tarjeta), negativo descuenta (efectivo), cero es el precio de góndola.
/// </summary>
public class TipoPrecio
{
    public Guid Id { get; set; }
    public string Nombre { get; set; }
    public decimal Ajuste { get; set; }
    public bool Activo { get; set; } = true;
    public int Orden { get; set; }

    /// <summary>Cantidad de formas de pago que lo usan (solo se completa en el listado).</summary>
    public int FormasPago { get; set; }

    public bool EsNuevo => Id == Guid.Empty;
    public string AjusteTexto => Ajuste == 0 ? "Sin ajuste"
        : Ajuste > 0 ? $"+{Ajuste.ToString("0.##")} % de recargo"
        : $"−{(-Ajuste).ToString("0.##")} % de descuento";
}

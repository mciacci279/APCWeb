using System.Globalization;
using System.Text;

namespace APC.Core.Datos;

/// <summary>
/// Arma archivos CSV que Excel abre bien en español: separador punto y coma,
/// números con coma decimal y marca UTF-8 al principio para que salgan las tildes.
/// </summary>
public static class Csv
{
    private static readonly CultureInfo Ar = new("es-AR");

    public static byte[] Armar(IEnumerable<string> encabezados, IEnumerable<IEnumerable<object>> filas)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", encabezados.Select(Campo)));
        foreach (var fila in filas)
            sb.AppendLine(string.Join(";", fila.Select(Valor)));

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();
    }

    private static string Valor(object v) => v switch
    {
        null => "",
        decimal d => d.ToString("0.00", Ar),
        double d => d.ToString("0.00", Ar),
        DateTime f => f.ToString("dd/MM/yyyy", Ar),
        bool b => b ? "Sí" : "No",
        _ => Campo(v.ToString())
    };

    private static string Campo(string texto)
    {
        texto = (texto ?? "").Replace("\r", " ").Replace("\n", " ");
        return texto.Contains(';') || texto.Contains('"') ? "\"" + texto.Replace("\"", "\"\"") + "\"" : texto;
    }
}

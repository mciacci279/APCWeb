using System.Globalization;
using System.Text.RegularExpressions;

namespace APC.Core.Servicios;

/// <summary>
/// Variables de fecha en la descripción de un producto: {mesletras}, {mesnumero}, {añoletras}, {añonumero}.
/// Se reemplazan con la fecha del comprobante, por ejemplo
/// "Servicio de Hosting del mes de {mesletras} de {añonumero}" → "Servicio de Hosting del mes de Noviembre de 2026".
/// No distingue mayúsculas y también acepta "anio"/"ano" en lugar de "año".
/// </summary>
public static class DescripcionConFechas
{
    /// <summary>Texto de ayuda para mostrar donde se carga o se ve la descripción.</summary>
    public const string Ayuda =
        "Podés usar {mesletras}, {mesnumero}, {añoletras} y {añonumero}: en el comprobante se reemplazan por el mes " +
        "y el año de su fecha. Ej.: \"Hosting de {mesletras} de {añonumero}\" → \"Hosting de Noviembre de 2026\".";

    private static readonly Regex Variable = new(@"\{\s*(mesletras|mesnumero|(?:año|anio|ano)letras|(?:año|anio|ano)numero)\s*\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] Meses =
        { "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio", "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre" };

    /// <summary>Tiene alguna de las variables.</summary>
    public static bool TieneVariables(string texto) => !string.IsNullOrEmpty(texto) && Variable.IsMatch(texto);

    public static string Reemplazar(string texto, DateTime fecha)
    {
        if (!TieneVariables(texto)) return texto;
        return Variable.Replace(texto, m =>
        {
            var v = m.Groups[1].Value.ToLowerInvariant();
            if (v == "mesletras") return Meses[fecha.Month - 1];
            if (v == "mesnumero") return fecha.Month.ToString("00", CultureInfo.InvariantCulture);
            if (v.EndsWith("letras")) return Capitalizar(EnLetras(fecha.Year));
            return fecha.Year.ToString(CultureInfo.InvariantCulture);
        });
    }

    private static string Capitalizar(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static readonly string[] Unidades =
        { "cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve", "diez",
          "once", "doce", "trece", "catorce", "quince", "dieciséis", "diecisiete", "dieciocho", "diecinueve",
          "veinte", "veintiuno", "veintidós", "veintitrés", "veinticuatro", "veinticinco", "veintiséis",
          "veintisiete", "veintiocho", "veintinueve" };
    private static readonly string[] Decenas = { "", "", "", "treinta", "cuarenta", "cincuenta", "sesenta", "setenta", "ochenta", "noventa" };
    private static readonly string[] Centenas =
        { "", "ciento", "doscientos", "trescientos", "cuatrocientos", "quinientos", "seiscientos", "setecientos", "ochocientos", "novecientos" };

    /// <summary>Número entero (0 a 999.999) en letras: 2026 → "dos mil veintiséis".</summary>
    public static string EnLetras(int n)
    {
        if (n < 30) return Unidades[n];
        if (n < 100) return Decenas[n / 10] + (n % 10 == 0 ? "" : " y " + Unidades[n % 10]);
        if (n == 100) return "cien";
        if (n < 1000) return Centenas[n / 100] + (n % 100 == 0 ? "" : " " + EnLetras(n % 100));
        if (n < 1_000_000)
        {
            var miles = n / 1000;
            var prefijo = miles == 1 ? "mil" : EnLetras(miles).Replace("uno", "un") + " mil";
            return prefijo + (n % 1000 == 0 ? "" : " " + EnLetras(n % 1000));
        }
        return n.ToString(CultureInfo.InvariantCulture);
    }
}

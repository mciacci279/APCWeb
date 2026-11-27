using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace APC.Web.Components.Shared;

/// <summary>
/// Resaltado simple de T-SQL para la vista previa de scripts: comentarios, textos, palabras clave, números y
/// los separadores GO. Todo el texto se escapa antes de armar el HTML, así que el script no puede inyectar nada.
/// Cada línea va en un &lt;span class="sql-l"&gt; para numerarla con CSS.
/// </summary>
public static class SqlResaltado
{
    private static readonly HashSet<string> Claves = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD","ALTER","AND","AS","ASC","BEGIN","BETWEEN","BIT","BY","CASE","CAST","CATCH","CLOSE","COMMIT","CONSTRAINT","CREATE",
        "CURSOR","DATE","DATETIME","DEALLOCATE","DECIMAL","DECLARE","DEFAULT","DELETE","DESC","DISTINCT","DROP","ELSE","END","EXEC",
        "EXECUTE","EXISTS","FETCH","FOR","FOREIGN","FROM","FUNCTION","GRANT","GROUP","HAVING","IF","IN","INDEX","INNER","INSERT","INT",
        "INTO","IS","JOIN","KEY","LEFT","LIKE","LOCAL","MERGE","NEXT","NOT","NULL","NVARCHAR","ON","OPEN","OR","ORDER","OUTER","PRIMARY",
        "PRINT","PROCEDURE","RAISERROR","REFERENCES","RETURN","RIGHT","ROLLBACK","SELECT","SET","TABLE","THEN","THROW","TOP","TRAN",
        "TRANSACTION","TRIGGER","TRY","UNIQUE","UNIQUEIDENTIFIER","UPDATE","USE","VALUES","VARCHAR","VIEW","WHEN","WHERE","WHILE","WITH",
        "MONEY","FLOAT","BIGINT","SMALLINT","TINYINT","CHAR","NCHAR","TEXT","VARBINARY","MAX","IDENTITY","CLUSTERED","NONCLUSTERED","INCLUDE"
    };

    private static readonly Regex Token = new(
        @"(?<com>--[^\n]*)|(?<blo>/\*.*?(\*/|$))|(?<str>N?'(?:[^']|'')*'?)|(?<num>\b\d+(\.\d+)?\b)|(?<pal>@{0,2}[A-Za-z_][A-Za-z0-9_]*)|(?<otro>.)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static MarkupString Html(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return new MarkupString("");
        var sb = new StringBuilder();
        var enBloque = false;
        foreach (var linea in sql.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append("<span class=\"sql-l\">");
            var resto = linea;
            if (enBloque)
            {
                var fin = resto.IndexOf("*/", StringComparison.Ordinal);
                if (fin < 0) { sb.Append("<span class=\"sql-c\">").Append(WebUtility.HtmlEncode(resto)).Append("</span></span>\n"); continue; }
                sb.Append("<span class=\"sql-c\">").Append(WebUtility.HtmlEncode(resto[..(fin + 2)])).Append("</span>");
                resto = resto[(fin + 2)..];
                enBloque = false;
            }
            if (resto.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("<span class=\"sql-go\">").Append(WebUtility.HtmlEncode(resto)).Append("</span></span>\n");
                continue;
            }
            foreach (Match m in Token.Matches(resto))
            {
                var t = WebUtility.HtmlEncode(m.Value);
                if (m.Groups["com"].Success) sb.Append("<span class=\"sql-c\">").Append(t).Append("</span>");
                else if (m.Groups["blo"].Success)
                {
                    sb.Append("<span class=\"sql-c\">").Append(t).Append("</span>");
                    if (!m.Value.EndsWith("*/", StringComparison.Ordinal)) enBloque = true;
                }
                else if (m.Groups["str"].Success) sb.Append("<span class=\"sql-s\">").Append(t).Append("</span>");
                else if (m.Groups["num"].Success) sb.Append("<span class=\"sql-n\">").Append(t).Append("</span>");
                else if (m.Groups["pal"].Success && Claves.Contains(m.Value)) sb.Append("<span class=\"sql-k\">").Append(t).Append("</span>");
                else if (m.Groups["pal"].Success && m.Value.StartsWith('@')) sb.Append("<span class=\"sql-v\">").Append(t).Append("</span>");
                else sb.Append(t);
            }
            sb.Append("</span>\n");
        }
        return new MarkupString(sb.ToString());
    }
}

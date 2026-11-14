using System.Collections.Concurrent;
using System.Text;

namespace APC.Web.Registro;

/// <summary>
/// Guarda los mensajes del sistema en archivos de texto, uno por día, para poder ver qué pasó
/// en el servidor sin entrar a la consola. Nunca interrumpe la aplicación: si el archivo no se
/// puede escribir, el error se ignora.
/// </summary>
[ProviderAlias("Archivo")]
public sealed class ArchivoLoggerProvider : ILoggerProvider
{
    private readonly string _carpeta;
    private readonly int _diasQueSeGuardan;
    private readonly object _candado = new();
    private readonly ConcurrentDictionary<string, ArchivoLogger> _loggers = new();
    private DateTime _ultimaLimpieza = DateTime.MinValue;

    public ArchivoLoggerProvider(string carpeta, int diasQueSeGuardan = 30)
    {
        _carpeta = carpeta;
        _diasQueSeGuardan = diasQueSeGuardan;
    }

    public ILogger CreateLogger(string categoria) =>
        _loggers.GetOrAdd(categoria, c => new ArchivoLogger(this, c));

    /// <summary>Agrega una línea al archivo del día. Si falla, no hace nada.</summary>
    public void Escribir(string texto)
    {
        try
        {
            lock (_candado)
            {
                Directory.CreateDirectory(_carpeta);
                BorrarViejos();
                File.AppendAllText(RutaDelDia(), texto, Encoding.UTF8);
            }
        }
        catch
        {
            // El registro nunca puede frenar la aplicación.
        }
    }

    public string RutaDelDia() => Path.Combine(_carpeta, $"apcweb-{DateTime.Now:yyyy-MM-dd}.log");

    private void BorrarViejos()
    {
        if ((DateTime.UtcNow - _ultimaLimpieza).TotalHours < 12) return;
        _ultimaLimpieza = DateTime.UtcNow;
        var limite = DateTime.Now.AddDays(-_diasQueSeGuardan);
        foreach (var archivo in Directory.GetFiles(_carpeta, "apcweb-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(archivo) < limite) File.Delete(archivo);
            }
            catch
            {
                // Si no se puede borrar uno viejo, seguimos.
            }
        }
    }

    public void Dispose() => _loggers.Clear();
}

internal sealed class ArchivoLogger : ILogger
{
    private readonly ArchivoLoggerProvider _proveedor;
    private readonly string _categoria;

    public ArchivoLogger(ArchivoLoggerProvider proveedor, string categoria)
    {
        _proveedor = proveedor;
        _categoria = categoria;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => SinAmbito.Instancia;

    public bool IsEnabled(LogLevel nivel) => nivel != LogLevel.None;

    public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception error,
        Func<TState, Exception, string> formatear)
    {
        if (!IsEnabled(nivel)) return;

        var texto = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .Append("  [").Append(Etiqueta(nivel)).Append("]  ")
            .Append(_categoria).Append("  ")
            .AppendLine(formatear(estado, error));

        if (error != null) texto.AppendLine(error.ToString());

        _proveedor.Escribir(texto.ToString());
    }

    private static string Etiqueta(LogLevel nivel) => nivel switch
    {
        LogLevel.Critical => "CRITICO",
        LogLevel.Error => "ERROR",
        LogLevel.Warning => "AVISO",
        LogLevel.Information => "INFO",
        LogLevel.Debug => "DEBUG",
        _ => "TRACE"
    };

    private sealed class SinAmbito : IDisposable
    {
        public static readonly SinAmbito Instancia = new();
        public void Dispose() { }
    }
}

using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace APC.Core.Servicios;

/// <summary>Configuración SMTP (sección "Smtp" de appsettings.Secretos.json).</summary>
public class SmtpOptions
{
    public string Host { get; set; }
    public int Puerto { get; set; } = 587;
    public string Usuario { get; set; }
    public string Clave { get; set; }
    /// <summary>Dirección que aparece como remitente, ej. no-responder@cm-tech.com.ar</summary>
    public string Remitente { get; set; }
    public string NombreRemitente { get; set; } = "APC Web · CM Tech";
}

public class EmailService
{
    private readonly IOptionsMonitor<SmtpOptions> _opt;

    public EmailService(IOptionsMonitor<SmtpOptions> opt) => _opt = opt;

    public bool Configurado => !string.IsNullOrWhiteSpace(_opt.CurrentValue.Host) && !string.IsNullOrWhiteSpace(_opt.CurrentValue.Remitente);

    public async Task EnviarAsync(string para, string nombre, string asunto, string html, string texto)
    {
        var o = _opt.CurrentValue;
        if (!Configurado) throw new InvalidOperationException("El envío de emails no está configurado (sección Smtp).");

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(o.NombreRemitente, o.Remitente));
        msg.To.Add(new MailboxAddress(nombre ?? para, para));
        msg.Subject = asunto;
        msg.Body = new BodyBuilder { HtmlBody = html, TextBody = texto }.ToMessageBody();

        using var smtp = new SmtpClient();
        // 465 = SSL directo; 587 (y otros) = STARTTLS si el servidor lo ofrece
        var seguridad = o.Puerto == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await smtp.ConnectAsync(o.Host, o.Puerto, seguridad);
        if (!string.IsNullOrWhiteSpace(o.Usuario))
            await smtp.AuthenticateAsync(o.Usuario, o.Clave);
        await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);
    }
}

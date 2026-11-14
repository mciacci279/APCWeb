using System.Security.Claims;
using APC.Arca;
using APC.Core.Modelos;
using APC.Core.Servicios;
using APC.Core.Tenants;
using APC.Web.Components;
using APC.Web.Registro;
using APC.Web.Sesion;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Conexión a la base central y otros datos sensibles: appsettings.Secretos.json (no se versiona)
builder.Configuration.AddJsonFile("appsettings.Secretos.json", optional: true, reloadOnChange: true);

// ---- Registro en archivo: C:\APCWeb\Logs\apcweb-aaaa-mm-dd.log ----
var carpetaLogs = builder.Configuration["APC:CarpetaLogs"];
if (string.IsNullOrWhiteSpace(carpetaLogs)) carpetaLogs = Path.Combine(AppContext.BaseDirectory, "Logs");
var registro = new ArchivoLoggerProvider(carpetaLogs);
builder.Logging.AddProvider(registro);

// Si la aplicación se cae del todo (por ejemplo al arrancar), que quede escrito el motivo.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    registro.Escribir($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [CRITICO]  La aplicación se detuvo{Environment.NewLine}{e.ExceptionObject}{Environment.NewLine}");

var central = builder.Configuration.GetConnectionString("Central");
if (string.IsNullOrWhiteSpace(central))
    throw new InvalidOperationException(
        "Falta la conexión a la base central (ConnectionStrings:Central). Cargala en appsettings.Secretos.json.");

builder.Services.Configure<TenantOptions>(builder.Configuration.GetSection("APC"));
builder.Services.PostConfigure<TenantOptions>(o => o.CentralConnectionString = central);
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/ingresar";
        o.LogoutPath = "/cuenta/salir";
        o.AccessDeniedPath = "/sin-permiso";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Cookie.Name = "apcweb";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton<TenantRegistry>();
builder.Services.AddSingleton<ArcaService>();
builder.Services.AddSingleton<CatalogoService>();
builder.Services.AddSingleton<UsuarioService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton<RecuperoClaveService>();
builder.Services.AddSingleton<EmpresaCentralService>();
builder.Services.AddSingleton<CertificadoService>();
builder.Services.AddSingleton<CajaService>();
builder.Services.AddSingleton<FacturacionService>();
builder.Services.AddSingleton<PresupuestoService>();
builder.Services.AddSingleton<ReporteService>();
builder.Services.AddSingleton<ClienteService>();
builder.Services.AddSingleton<PadronService>();
builder.Services.AddSingleton<SucursalService>();
builder.Services.AddSingleton<TableroService>();
builder.Services.AddSingleton<CobranzaService>();
builder.Services.AddSingleton<ProductoService>();
builder.Services.AddSingleton<ImportadorProductosService>();
builder.Services.AddScoped<SesionService>();

var app = builder.Build();

app.Logger.LogInformation("APC Web iniciada. El registro se guarda en {carpeta}", carpetaLogs);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ---- Ingreso / salida (formulario HTML clásico, fuera del circuito de Blazor) ----
app.MapPost("/cuenta/ingresar", async (HttpContext ctx, IAntiforgery af, UsuarioService usuarios) =>
{
    await af.ValidateRequestAsync(ctx);
    var form = await ctx.Request.ReadFormAsync();
    string empresa = form["empresa"], usuario = form["usuario"], clave = form["clave"], codigo = form["codigo"];
    var e = Uri.EscapeDataString(empresa ?? "");

    IngresoResultado r;
    try
    {
        r = await usuarios.ValidarAsync(empresa, usuario, clave, ctx.Connection.RemoteIpAddress?.ToString(), codigo);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error al validar el ingreso");
        var detalle = app.Environment.IsDevelopment() ? ex.GetBaseException().Message : "";
        return Results.Redirect($"/ingresar?error=conexion&e={e}&d={Uri.EscapeDataString(detalle)}");
    }

    switch (r.Estado)
    {
        case EstadoIngreso.EmpresaNoExiste: return Results.Redirect($"/ingresar?error=empresa&e={e}");
        case EstadoIngreso.EmpresaSuspendida: return Results.Redirect($"/ingresar?error=suspendida&e={e}");
        case EstadoIngreso.Bloqueado: return Results.Redirect($"/ingresar?error=bloqueado&e={e}");
        case EstadoIngreso.ClaveIncorrecta: return Results.Redirect($"/ingresar?error=clave&e={e}");
        case EstadoIngreso.FaltaCodigo: return Results.Redirect($"/ingresar?error=falta-codigo&e={e}&u={Uri.EscapeDataString(usuario ?? "")}");
        case EstadoIngreso.CodigoIncorrecto: return Results.Redirect($"/ingresar?error=codigo&e={e}&u={Uri.EscapeDataString(usuario ?? "")}");
    }

    var u = r.Usuario;
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, u.Id.ToString()),
        new(ClaimTypes.Name, u.NombreUsuario),
        new("nombre", string.IsNullOrWhiteSpace(u.NombreCompleto) ? u.NombreUsuario : u.NombreCompleto),
        new("tenant", r.Tenant?.Codigo ?? "")
    };
    switch (u.Rol)
    {
        case RolUsuario.SuperAdmin:
            claims.Add(new Claim(ClaimTypes.Role, SesionService.RolSuperAdmin));
            break;
        case RolUsuario.Admin:
            claims.Add(new Claim(ClaimTypes.Role, SesionService.RolAdmin));
            claims.Add(new Claim(ClaimTypes.Role, SesionService.RolEmpleado));
            break;
        default:
            claims.Add(new Claim(ClaimTypes.Role, SesionService.RolEmpleado));
            break;
    }

    var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Redirect(u.Rol == RolUsuario.SuperAdmin ? "/admin/empresas" : "/");
}).DisableAntiforgery(); // se valida a mano arriba

app.MapPost("/cuenta/salir", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/ingresar");
});

// ---- Recupero de clave por email ----
app.MapPost("/cuenta/recuperar", async (HttpContext ctx, IAntiforgery af, RecuperoClaveService recupero, TenantRegistry tenants) =>
{
    await af.ValidateRequestAsync(ctx);
    var form = await ctx.Request.ReadFormAsync();
    string empresa = form["empresa"], usuario = form["usuario"];
    // El link del email usa la dirección pública configurada (evita links armados con un Host falso)
    var urlBase = !string.IsNullOrWhiteSpace(tenants.UrlPublica)
        ? tenants.UrlPublica
        : $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}/";
    try
    {
        await recupero.SolicitarAsync(empresa, usuario, urlBase, ctx.Connection.RemoteIpAddress?.ToString());
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error en el recupero de clave");
        return Results.Redirect("/recuperar?error=envio&e=" + Uri.EscapeDataString(empresa ?? ""));
    }
    // Misma respuesta exista o no el usuario
    return Results.Redirect("/recuperar?enviado=1&e=" + Uri.EscapeDataString(empresa ?? ""));
}).DisableAntiforgery();

app.MapPost("/cuenta/restablecer", async (HttpContext ctx, IAntiforgery af, RecuperoClaveService recupero) =>
{
    await af.ValidateRequestAsync(ctx);
    var form = await ctx.Request.ReadFormAsync();
    string token = form["t"], clave = form["clave"], clave2 = form["clave2"];
    var t = Uri.EscapeDataString(token ?? "");
    if (clave != clave2) return Results.Redirect($"/restablecer?t={t}&error=" + Uri.EscapeDataString("Las claves no coinciden."));
    var (error, empresa) = await recupero.RestablecerAsync(token, clave, ctx.Connection.RemoteIpAddress?.ToString());
    if (error != null) return Results.Redirect($"/restablecer?t={t}&error=" + Uri.EscapeDataString(error));
    return Results.Redirect("/ingresar?ok=clave&e=" + Uri.EscapeDataString(empresa ?? ""));
}).DisableAntiforgery();

// Sesión de una empresa que fue suspendida mientras el usuario estaba adentro
app.MapGet("/cuenta/expirada", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/ingresar?error=suspendida");
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

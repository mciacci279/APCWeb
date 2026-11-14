using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace APC.Arca
{
    /// <summary>Datos que ARCA devuelve de una persona (padrón A13).</summary>
    public class PersonaPadron
    {
        public bool Encontrada { get; set; }
        public string Error { get; set; }

        public string Cuit { get; set; }
        /// <summary>"FISICA" o "JURIDICA".</summary>
        public string TipoPersona { get; set; }
        public string RazonSocial { get; set; }
        public string Apellido { get; set; }
        public string Nombre { get; set; }
        public string EstadoClave { get; set; }

        public string Domicilio { get; set; }
        public string Localidad { get; set; }
        public string CodigoPostal { get; set; }
        public string Provincia { get; set; }

        /// <summary>"RESPONSABLE_INSCRIPTO", "MONOTRIBUTO", "EXENTO" o "CONSUMIDOR_FINAL".</summary>
        public string CondicionIva { get; set; }

        /// <summary>Nombre para mostrar: razón social, o apellido y nombre.</summary>
        public string NombreCompleto =>
            !string.IsNullOrWhiteSpace(RazonSocial)
                ? RazonSocial
                : string.Join(" ", new[] { Apellido, Nombre }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>
    /// Consulta el padrón de ARCA (servicio ws_sr_padron_a13) para traer los datos de un CUIT.
    /// El certificado de la empresa tiene que estar habilitado para ese servicio en ARCA
    /// (Administrador de Relaciones), igual que se hace con wsfe.
    /// </summary>
    public static class PadronA13Client
    {
        public const string Servicio = "ws_sr_padron_a13";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public static string Url(bool produccion) => produccion
            ? "https://aws.afip.gov.ar/sr-padron/webservices/personaServiceA13"
            : "https://awshomo.afip.gov.ar/sr-padron/webservices/personaServiceA13";

        /// <param name="cuitConsultado">CUIT a buscar (11 dígitos).</param>
        /// <param name="cuitRepresentada">CUIT de la empresa que consulta (la dueña del certificado).</param>
        public static async Task<PersonaPadron> ConsultarAsync(string cuitConsultado, long cuitRepresentada,
            string certificadoPath, string clavePath, string urlWsaa, string urlPadron, string carpetaCache)
        {
            var persona = new PersonaPadron { Cuit = cuitConsultado };

            WsaaAuth.TicketAcceso ta;
            try
            {
                ta = WsaaAuth.ObtenerTicket(Servicio, certificadoPath, clavePath, urlWsaa, carpetaCache);
            }
            catch (Exception ex)
            {
                persona.Error = "No se pudo autenticar contra ARCA para consultar el padrón. " +
                                "Verificá que el certificado esté habilitado para el servicio «Consulta al padrón A13» " +
                                "en el Administrador de Relaciones de ARCA. Detalle: " + ex.Message;
                return persona;
            }

            string sobre =
                "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:a13=\"http://a13.soap.ws.server.puc.sr/\">" +
                "<soapenv:Header/><soapenv:Body><a13:getPersona>" +
                "<token>" + ta.Token + "</token>" +
                "<sign>" + ta.Sign + "</sign>" +
                "<cuitRepresentada>" + cuitRepresentada + "</cuitRepresentada>" +
                "<idPersona>" + cuitConsultado + "</idPersona>" +
                "</a13:getPersona></soapenv:Body></soapenv:Envelope>";

            string respuesta;
            try
            {
                using (var contenido = new StringContent(sobre, Encoding.UTF8, "text/xml"))
                {
                    contenido.Headers.Add("SOAPAction", "\"\"");
                    var r = await Http.PostAsync(urlPadron, contenido);
                    respuesta = await r.Content.ReadAsStringAsync();
                }
            }
            catch (Exception ex)
            {
                persona.Error = "No se pudo conectar con el padrón de ARCA: " + ex.Message;
                return persona;
            }

            return Interpretar(respuesta, persona);
        }

        private static PersonaPadron Interpretar(string xml, PersonaPadron persona)
        {
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (Exception) { persona.Error = "ARCA devolvió una respuesta que no se pudo leer."; return persona; }

            var falla = Elemento(doc.Root, "faultstring");
            if (falla != null)
            {
                var texto = falla.Value ?? "";
                persona.Error = texto.IndexOf("No existe persona", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "ARCA no tiene datos para ese CUIT."
                    : "ARCA rechazó la consulta: " + texto;
                return persona;
            }

            var generales = Elemento(doc.Root, "datosGenerales");
            if (generales == null)
            {
                persona.Error = "ARCA no devolvió datos para ese CUIT.";
                return persona;
            }

            persona.Encontrada = true;
            persona.TipoPersona = Texto(generales, "tipoPersona");
            persona.RazonSocial = Texto(generales, "razonSocial");
            persona.Apellido = Texto(generales, "apellido");
            persona.Nombre = Texto(generales, "nombre");
            persona.EstadoClave = Texto(generales, "estadoClave");

            var domicilio = Elemento(generales, "domicilioFiscal");
            if (domicilio != null)
            {
                persona.Domicilio = Texto(domicilio, "direccion");
                persona.Localidad = Texto(domicilio, "localidad") ?? Texto(domicilio, "descripcionProvincia");
                persona.CodigoPostal = Texto(domicilio, "codPostal");
                persona.Provincia = Texto(domicilio, "descripcionProvincia");
            }

            persona.CondicionIva = DeducirCondicion(doc.Root);
            return persona;
        }

        /// <summary>
        /// ARCA no devuelve la condición frente al IVA directamente: se deduce de los impuestos.
        /// Monotributo si está inscripto en el régimen simplificado; responsable inscripto si tiene
        /// el impuesto 30 (IVA); exento si tiene el 32.
        /// </summary>
        private static string DeducirCondicion(XElement raiz)
        {
            if (Elemento(raiz, "datosMonotributo") != null) return "MONOTRIBUTO";

            var impuestos = Todos(raiz, "idImpuesto").Select(e => e.Value?.Trim()).ToList();
            if (impuestos.Contains("30")) return "RESPONSABLE_INSCRIPTO";
            if (impuestos.Contains("32")) return "EXENTO";
            return "CONSUMIDOR_FINAL";
        }

        // El SOAP viene con varios espacios de nombres: buscamos por nombre local.
        private static XElement Elemento(XElement raiz, string nombre) =>
            raiz?.Descendants().FirstOrDefault(e => e.Name.LocalName == nombre);

        private static System.Collections.Generic.IEnumerable<XElement> Todos(XElement raiz, string nombre) =>
            raiz?.Descendants().Where(e => e.Name.LocalName == nombre) ?? Enumerable.Empty<XElement>();

        private static string Texto(XElement raiz, string nombre)
        {
            var e = Elemento(raiz, nombre);
            var v = e?.Value?.Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }
}

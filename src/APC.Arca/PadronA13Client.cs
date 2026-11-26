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
        /// <summary>Tipo de clave: "CUIT", "CUIL" o "CDI".</summary>
        public string TipoClave { get; set; }

        public string Domicilio { get; set; }
        public string Localidad { get; set; }
        public string CodigoPostal { get; set; }
        public string Provincia { get; set; }

        /// <summary>Respuesta tal cual de ARCA (padrón A13), para mostrarla en "Ver respuesta de ARCA".</summary>
        public string RespuestaA13 { get; set; }
        /// <summary>Respuesta de la constancia de inscripción (A5), si se pudo consultar.</summary>
        public string RespuestaA5 { get; set; }

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

            persona.RespuestaA13 = respuesta;
            persona = Interpretar(respuesta, persona);

            // El padrón A13 trae los datos de identificación pero no los impuestos: la condición frente
            // al IVA se pide a la constancia de inscripción (ws_sr_constancia_inscripcion). Si el
            // certificado no está habilitado para ese servicio, se sigue sin ella.
            if (persona.Encontrada && persona.CondicionIva == null)
            {
                try
                {
                    var urlA5 = urlPadron.Replace("personaServiceA13", "personaServiceA5");
                    var ta5 = WsaaAuth.ObtenerTicket(ServicioConstancia, certificadoPath, clavePath, urlWsaa, carpetaCache);
                    string sobre5 =
                        "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:a5=\"http://a5.soap.ws.server.puc.sr/\">" +
                        "<soapenv:Header/><soapenv:Body><a5:getPersona_v2>" +
                        "<token>" + ta5.Token + "</token>" +
                        "<sign>" + ta5.Sign + "</sign>" +
                        "<cuitRepresentada>" + cuitRepresentada + "</cuitRepresentada>" +
                        "<idPersona>" + cuitConsultado + "</idPersona>" +
                        "</a5:getPersona_v2></soapenv:Body></soapenv:Envelope>";
                    using (var contenido = new StringContent(sobre5, Encoding.UTF8, "text/xml"))
                    {
                        contenido.Headers.Add("SOAPAction", "\"\"");
                        var r = await Http.PostAsync(urlA5, contenido);
                        var xml5 = await r.Content.ReadAsStringAsync();
                        persona.RespuestaA5 = xml5;
                        var doc5 = XDocument.Parse(xml5);
                        if (Elemento(doc5.Root, "faultstring") == null && Elemento(doc5.Root, "datosGenerales") != null)
                            persona.CondicionIva = DeducirCondicion(doc5.Root);
                    }
                }
                catch (Exception ex)
                {
                    // Sin la constancia de inscripción: la condición queda sin informar.
                    if (persona.RespuestaA5 == null) persona.RespuestaA5 = "No se pudo consultar la constancia de inscripción (ws_sr_constancia_inscripcion): " + ex.Message;
                }
            }
            return persona;
        }

        /// <summary>Servicio de la constancia de inscripción (padrón A5): trae los impuestos.</summary>
        public const string ServicioConstancia = "ws_sr_constancia_inscripcion";

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
                // Padrón A13: los datos vienen en <persona>, con uno o más <domicilio> (el FISCAL primero).
                var p13 = Elemento(doc.Root, "persona");
                if (p13 == null || (Texto(p13, "razonSocial") == null && Texto(p13, "apellido") == null && Texto(p13, "nombre") == null))
                {
                    persona.Error = Elemento(doc.Root, "personaReturn") != null || Elemento(doc.Root, "getPersonaResponse") != null
                        ? "ARCA no tiene datos para ese CUIT (no está en el padrón)."
                        : "ARCA respondió algo inesperado: " + Resumen(xml);
                    return persona;
                }
                persona.Encontrada = true;
                persona.TipoPersona = Texto(p13, "tipoPersona");
                persona.RazonSocial = Texto(p13, "razonSocial");
                persona.Apellido = Texto(p13, "apellido");
                persona.Nombre = Texto(p13, "nombre");
                persona.EstadoClave = Texto(p13, "estadoClave");
                persona.TipoClave = Texto(p13, "tipoClave");
                var doms = p13.Elements().Where(e => e.Name.LocalName == "domicilio").ToList();
                var fiscal = doms.FirstOrDefault(d => string.Equals(Texto(d, "tipoDomicilio"), "FISCAL", StringComparison.OrdinalIgnoreCase))
                             ?? doms.FirstOrDefault();
                if (fiscal != null)
                {
                    persona.Domicilio = Texto(fiscal, "direccion");
                    persona.Localidad = Texto(fiscal, "localidad") ?? Texto(fiscal, "descripcionProvincia");
                    persona.CodigoPostal = Texto(fiscal, "codigoPostal") ?? Texto(fiscal, "codPostal");
                    persona.Provincia = Texto(fiscal, "descripcionProvincia");
                }
                // Si la respuesta trae impuestos, se deduce; si no, queda para la constancia de inscripción.
                persona.CondicionIva = Todos(doc.Root, "idImpuesto").Any() || Elemento(doc.Root, "datosMonotributo") != null
                    ? DeducirCondicion(doc.Root) : null;
                return persona;
            }

            persona.Encontrada = true;
            persona.TipoPersona = Texto(generales, "tipoPersona");
            persona.RazonSocial = Texto(generales, "razonSocial");
            persona.Apellido = Texto(generales, "apellido");
            persona.Nombre = Texto(generales, "nombre");
            persona.EstadoClave = Texto(generales, "estadoClave");
            persona.TipoClave = Texto(generales, "tipoClave");

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

        /// <summary>
        /// La respuesta de ARCA como filas legibles (sección / campo / valor), sin el sobre SOAP ni el token.
        /// </summary>
        public static System.Collections.Generic.List<(int Nivel, string Campo, string Valor)> Legible(string xml)
        {
            var filas = new System.Collections.Generic.List<(int, string, string)>();
            XDocument doc;
            try { doc = XDocument.Parse(xml ?? ""); }
            catch (Exception) { filas.Add((0, "Respuesta", xml)); return filas; }
            var cuerpo = doc.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Body") ?? doc.Root;
            void Recorrer(XElement e, int nivel)
            {
                foreach (var h in e.Elements())
                {
                    var nombre = h.Name.LocalName;
                    if (nombre == "token" || nombre == "sign") continue;
                    if (h.HasElements) { filas.Add((nivel, nombre, null)); Recorrer(h, nivel + 1); }
                    else filas.Add((nivel, nombre, h.Value?.Trim()));
                }
            }
            if (cuerpo != null) Recorrer(cuerpo, 0);
            return filas;
        }

        /// <summary>Primeros caracteres de una respuesta rara, sin etiquetas, para mostrar en el error.</summary>
        private static string Resumen(string xml)
        {
            var texto = System.Text.RegularExpressions.Regex.Replace(xml ?? "", "<[^>]+>", " ");
            texto = System.Text.RegularExpressions.Regex.Replace(texto, "\\s+", " ").Trim();
            return texto.Length > 200 ? texto.Substring(0, 200) + "…" : (texto.Length == 0 ? "(vacío)" : texto);
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

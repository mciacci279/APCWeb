using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.Utilities.Collections;
using System.Collections.Generic;

namespace APC.Arca
{
    /// <summary>
    /// Autenticación contra el WSAA (Web Service de Autenticación y Autorización) de ARCA/AFIP.
    /// Reemplaza el "lwsfev1.login(certificado, clave, URLWSAA)" del DLL de terceros.
    ///
    /// Requiere el paquete NuGet "BouncyCastle.Cryptography" (Install-Package BouncyCastle.Cryptography)
    /// para firmar el TRA en formato CMS/PKCS#7 a partir de un certificado y clave privada en PEM
    /// (los mismos archivos que ya usás hoy como CertificadoFile / ClaveFile).
    ///
    /// IMPORTANTE: AFIP no permite pedir un TA (Ticket de Acceso) nuevo si ya existe uno vigente
    /// para el mismo servicio+CUIT (devuelve un SOAP Fault). Por eso este helper cachea el TA en
    /// disco y sólo pide uno nuevo cuando el cacheado ya venció o está por vencer.
    /// </summary>
    public static class WsaaAuth
    {
        public class TicketAcceso
        {
            public string Token;
            public string Sign;
            public DateTime Expiracion;
        }

        /// <summary>
        /// Namespace del WSAA (fijo, no cambia entre homologación y producción).
        /// </summary>
        private const string WsaaNamespace = "http://wsaa.view.sua.dvadac.desein.afip.gov";

        /// <summary>
        /// Obtiene un Token/Sign válido para el servicio indicado (por ejemplo "wsfe"),
        /// reutilizando el TA cacheado si todavía tiene más de 10 minutos de vida.
        /// </summary>
        /// <param name="servicio">Nombre del servicio AFIP, ej. "wsfe".</param>
        /// <param name="certificadoPath">Ruta al certificado .crt/.pem (el mismo CertificadoFile de hoy).</param>
        /// <param name="clavePath">Ruta a la clave privada .key/.pem (el mismo ClaveFile de hoy).</param>
        /// <param name="urlWSAA">URL del WSAA (homologación o producción, el mismo URLWSAA de hoy).</param>
        /// <param name="carpetaCache">Carpeta donde guardar el TA cacheado (ej. Path.GetTempPath() o App_Data).</param>
        public static TicketAcceso ObtenerTicket(string servicio, string certificadoPath, string clavePath,
            string urlWSAA, string carpetaCache)
        {
            Directory.CreateDirectory(carpetaCache);
            string cacheFile = Path.Combine(carpetaCache, $"TA_{servicio}.xml");

            var cacheado = LeerCache(cacheFile);
            if (cacheado != null && cacheado.Expiracion > DateTime.Now.AddMinutes(10))
                return cacheado;

            string tra = GenerarTRA(servicio);
            byte[] cms = FirmarCMS(tra, certificadoPath, clavePath);
            string cmsBase64 = Convert.ToBase64String(cms);

            string loginTicketResponseXml;
            try
            {
                loginTicketResponseXml = LlamarLoginCms(urlWSAA, cmsBase64);
            }
            catch (WebException wex)
            {
                string detalle = LeerCuerpoError(wex);
                // Si AFIP dice que ya hay un TA vigente y por algún motivo no lo teníamos cacheado
                // (ej. se corrió en otro proceso/servidor), no hay forma de recuperarlo: hay que
                // esperar a que venza. Se informa el motivo real en vez de un error genérico.
                throw new Exception("Error llamando a WSAA loginCms: " + detalle, wex);
            }

            var ticket = ParsearRespuesta(loginTicketResponseXml);
            GuardarCache(cacheFile, ticket);
            return ticket;
        }

        private static string GenerarTRA(string servicio)
        {
            DateTime ahora = DateTime.Now;
            string uniqueId = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString();
            string genTime = ahora.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:sszzz");
            string expTime = ahora.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:sszzz");

            return
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<loginTicketRequest version=""1.0"">
  <header>
    <uniqueId>{uniqueId}</uniqueId>
    <generationTime>{genTime}</generationTime>
    <expirationTime>{expTime}</expirationTime>
  </header>
  <service>{servicio}</service>
</loginTicketRequest>";
        }

        /// <summary>
        /// Firma el TRA en formato CMS/PKCS#7 "attached" (SHA-256), igual que lo hacen
        /// todas las implementaciones de referencia de AFIP (pyafipws, etc).
        /// Acepta certificado y clave privada en formato PEM (-----BEGIN CERTIFICATE----- / -----BEGIN (RSA) PRIVATE KEY-----).
        /// </summary>
        private static byte[] FirmarCMS(string traXml, string certificadoPath, string clavePath)
        {
            X509Certificate cert;
            using (var certReader = File.OpenText(certificadoPath))
                cert = (X509Certificate)new PemReader(certReader).ReadObject();

            AsymmetricKeyParameter privateKey;
            using (var keyReader = File.OpenText(clavePath))
            {
                object keyObj = new PemReader(keyReader).ReadObject();
                privateKey = keyObj is AsymmetricCipherKeyPair pair ? pair.Private : (AsymmetricKeyParameter)keyObj;
            }

            var certList = new List<X509Certificate> { cert };
            var store = CollectionUtilities.CreateStore(certList);

            var generator = new CmsSignedDataGenerator();
            generator.AddSigner(privateKey, cert, CmsSignedDataGenerator.EncryptionRsa, CmsSignedDataGenerator.DigestSha256);
            generator.AddCertificates(store);

            var contenido = new CmsProcessableByteArray(Encoding.UTF8.GetBytes(traXml));
            CmsSignedData signedData = generator.Generate(contenido, true); // true = CMS "attached" (incluye el contenido)

            return signedData.GetEncoded();
        }

        private static string LlamarLoginCms(string wsaaUrl, string cmsBase64)
        {
            string soapRequest =
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <loginCms xmlns=""{WsaaNamespace}"">
      <in0>{cmsBase64}</in0>
    </loginCms>
  </soap:Body>
</soap:Envelope>";

            var req = (HttpWebRequest)WebRequest.Create(wsaaUrl);
            req.Method = "POST";
            req.ContentType = "text/xml; charset=utf-8";
            req.Headers.Add("SOAPAction", "\"\"");

            byte[] bytes = Encoding.UTF8.GetBytes(soapRequest);
            req.ContentLength = bytes.Length;
            using (var reqStream = req.GetRequestStream())
                reqStream.Write(bytes, 0, bytes.Length);

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string soapResponseXml = reader.ReadToEnd();
                var doc = XDocument.Parse(soapResponseXml);
                var nodo = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "loginCmsReturn");
                if (nodo == null)
                    throw new Exception("Respuesta de WSAA sin loginCmsReturn: " + soapResponseXml);
                return nodo.Value;
            }
        }

        private static string LeerCuerpoError(WebException wex)
        {
            if (wex.Response == null) return wex.Message;
            using (var reader = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
            {
                string body = reader.ReadToEnd();
                try
                {
                    var doc = XDocument.Parse(body);
                    var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring");
                    return fault != null ? fault.Value : body;
                }
                catch
                {
                    return body;
                }
            }
        }

        private static TicketAcceso ParsearRespuesta(string loginTicketResponseXml)
        {
            var doc = XDocument.Parse(loginTicketResponseXml);
            string token = doc.Descendants().First(e => e.Name.LocalName == "token").Value;
            string sign = doc.Descendants().First(e => e.Name.LocalName == "sign").Value;
            string expStr = doc.Descendants().First(e => e.Name.LocalName == "expirationTime").Value;

            return new TicketAcceso
            {
                Token = token,
                Sign = sign,
                Expiracion = DateTime.Parse(expStr)
            };
        }

        private static TicketAcceso LeerCache(string cacheFile)
        {
            if (!File.Exists(cacheFile)) return null;
            try
            {
                var doc = XDocument.Load(cacheFile);
                return new TicketAcceso
                {
                    Token = doc.Root.Element("Token").Value,
                    Sign = doc.Root.Element("Sign").Value,
                    Expiracion = DateTime.Parse(doc.Root.Element("Expiracion").Value)
                };
            }
            catch
            {
                return null; // cache corrupto -> se ignora y se pide un TA nuevo
            }
        }

        private static void GuardarCache(string cacheFile, TicketAcceso ticket)
        {
            var doc = new XDocument(
                new XElement("TicketAcceso",
                    new XElement("Token", ticket.Token),
                    new XElement("Sign", ticket.Sign),
                    new XElement("Expiracion", ticket.Expiracion.ToString("o"))));
            doc.Save(cacheFile);
        }
    }
}

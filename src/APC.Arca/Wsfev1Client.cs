using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace APC.Arca
{
    /// <summary>
    /// Cliente propio del WSFEv1 (Factura Electrónica) de ARCA/AFIP, por SOAP directo,
    /// pensado como reemplazo "drop-in" de la clase "wsfev1" del DLL de terceros (FEAFIP.dll).
    ///
    /// Expone las mismas propiedades/métodos que ya usás (CUIT, URL, login, SFRecuperaLastCMP,
    /// SFLastCMP, Reset, AgregaFactura, AgregaIVA, Autorizar, AutorizarRespuesta,
    /// AutorizarRespuestaObs, ErrorDesc) para que el resto de tu código cambie lo mínimo posible.
    /// La diferencia real es que AgregaFactura ahora recibe un parámetro más: condicionIVAReceptorId.
    ///
    /// Requiere el paquete NuGet "BouncyCastle.Cryptography" (usado por WsaaAuth.cs).
    ///
    /// NOTA: al igual que el DLL original en tu código, esta clase procesa UN comprobante por vez
    /// (Reset -> AgregaFactura -> [AgregaIVA...] -> Autorizar -> AutorizarRespuesta). Si en algún
    /// momento necesitás autorizar varios comprobantes en un mismo lote convendría extenderla.
    /// </summary>
    public class Wsfev1Client
    {
        private const string WsfeNamespace = "http://ar.gov.afip.dif.FEV1/";

        public double CUIT { get; set; }
        public string URL { get; set; }
        public string ErrorDesc { get; private set; }
        public long SFLastCMP { get; private set; }

        private string _carpetaCacheTA;
        private string _token;
        private string _sign;
        private long _cuitAuth;

        private FeDetReqData _detalleActual;
        private FeDetRespData _ultimaRespuesta;

        /// <param name="carpetaCacheTA">Carpeta donde cachear el ticket de acceso de WSAA
        /// (ver WsaaAuth.cs). Si no se indica, usa la carpeta temporal del sistema.</param>
        public Wsfev1Client(string carpetaCacheTA = null)
        {
            _carpetaCacheTA = carpetaCacheTA ?? Path.GetTempPath();
        }

        public bool login(string certificado, string clave, string urlWSAA)
        {
            try
            {
                var ticket = WsaaAuth.ObtenerTicket("wsfe", certificado, clave, urlWSAA, _carpetaCacheTA);
                _token = ticket.Token;
                _sign = ticket.Sign;
                _cuitAuth = (long)CUIT;
                return true;
            }
            catch (Exception ex)
            {
                ErrorDesc = ex.Message;
                return false;
            }
        }

        public bool SFRecuperaLastCMP(int ptoVta, int tipoComp)
        {
            try
            {
                string body =
$@"<FECompUltimoAutorizado xmlns=""{WsfeNamespace}"">
  <Auth>
    <Token>{_token}</Token>
    <Sign>{_sign}</Sign>
    <Cuit>{_cuitAuth}</Cuit>
  </Auth>
  <PtoVta>{ptoVta}</PtoVta>
  <CbteTipo>{tipoComp}</CbteTipo>
</FECompUltimoAutorizado>";

                var doc = Llamar("FECompUltimoAutorizado", body);

                var errores = ExtraerErrores(doc);
                if (errores.Count > 0)
                {
                    ErrorDesc = string.Join(" | ", errores);
                    return false;
                }

                var nro = doc.Descendants().First(e => e.Name.LocalName == "CbteNro").Value;
                SFLastCMP = long.Parse(nro, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                ErrorDesc = ex.Message;
                return false;
            }
        }

        public void Reset()
        {
            _detalleActual = null;
            _ultimaRespuesta = null;
        }

        /// <summary>
        /// Mismos parámetros que ya usabas contra FEAFIP.dll, más "condicionIVAReceptorId" al final:
        /// la condición frente al IVA del receptor del comprobante (obligatoria por RG ARCA 5616).
        /// Ver WsfeCondicionIvaReceptor para la tabla de códigos válidos según tipo de comprobante.
        /// </summary>
        public void AgregaFactura(int concepto, int tipoDoc, double nroDoc, double cbteDesde, double cbteHasta,
            string cbteFch, double impTotal, double impTotConc, double impNeto, double impOpEx,
            string fchServDesde, string fchServHasta, string fchVtoPago, string monId, double monCotiz,
            int condicionIVAReceptorId)
        {
            _detalleActual = new FeDetReqData
            {
                Concepto = concepto,
                DocTipo = tipoDoc,
                DocNro = (long)nroDoc,
                CbteDesde = (long)cbteDesde,
                CbteHasta = (long)cbteHasta,
                CbteFch = cbteFch,
                ImpTotal = impTotal,
                ImpTotConc = impTotConc,
                ImpNeto = impNeto,
                ImpOpEx = impOpEx,
                FchServDesde = fchServDesde,
                FchServHasta = fchServHasta,
                FchVtoPago = fchVtoPago,
                MonId = monId,
                MonCotiz = monCotiz,
                CondicionIVAReceptorId = condicionIVAReceptorId,
                Ivas = new List<AlicIvaData>(),
                CbtesAsoc = new List<CbteAsocData>()
            };
        }

        /// <summary>
        /// Comprobante asociado (obligatorio en notas de crédito y débito): la factura que se anula o ajusta.
        /// fecha en formato yyyyMMdd; cuit = CUIT del emisor del comprobante asociado.
        /// </summary>
        public void AgregaCompAsoc(int tipo, int ptoVta, long nro, long cuit, string fecha)
        {
            if (_detalleActual == null)
                throw new InvalidOperationException("Llamá primero a AgregaFactura().");
            _detalleActual.CbtesAsoc.Add(new CbteAsocData { Tipo = tipo, PtoVta = ptoVta, Nro = nro, Cuit = cuit, CbteFch = fecha });
        }

        public void AgregaIVA(int id, double baseImp, double importe)
        {
            if (_detalleActual == null)
                throw new InvalidOperationException("Llamá primero a AgregaFactura().");
            _detalleActual.Ivas.Add(new AlicIvaData { Id = id, BaseImp = baseImp, Importe = importe });
        }

        public bool Autorizar(int ptoVta, int tipoComp)
        {
            try
            {
                if (_detalleActual == null)
                    throw new InvalidOperationException("No hay ningún comprobante cargado (AgregaFactura).");

                var det = _detalleActual;

                var sbIva = new StringBuilder();
                if (det.Ivas.Count > 0)
                {
                    sbIva.Append("<Iva>");
                    foreach (var iva in det.Ivas)
                        sbIva.Append(
                            $"<AlicIva><Id>{iva.Id}</Id><BaseImp>{Fmt(iva.BaseImp)}</BaseImp><Importe>{Fmt(iva.Importe)}</Importe></AlicIva>");
                    sbIva.Append("</Iva>");
                }

                // FchServDesde/Hasta/VtoPago sólo corresponden si Concepto = 2 (Servicios) o 3 (Productos y Servicios)
                string fechasServicio = det.Concepto != 1
                    ? $"<FchServDesde>{det.FchServDesde}</FchServDesde><FchServHasta>{det.FchServHasta}</FchServHasta><FchVtoPago>{det.FchVtoPago}</FchVtoPago>"
                    : "";

                // MonCotiz es obligatorio siempre (para PES va en 1). Mandarlo vacío/omitido
                // dispara el error 10038 de ARCA aunque la moneda sea pesos.
                string monCotiz = $"<MonCotiz>{Fmt(det.MonCotiz, 6)}</MonCotiz>";

                double impIVA = det.Ivas.Sum(i => i.Importe);

                // Según el manual de ARCA, CbtesAsoc va después de CondicionIVAReceptorId y antes de Iva
                var sbAsoc = new StringBuilder();
                if (det.CbtesAsoc.Count > 0)
                {
                    sbAsoc.Append("<CbtesAsoc>");
                    foreach (var a in det.CbtesAsoc)
                    {
                        sbAsoc.Append($"<CbteAsoc><Tipo>{a.Tipo}</Tipo><PtoVta>{a.PtoVta}</PtoVta><Nro>{a.Nro}</Nro>");
                        if (a.Cuit > 0) sbAsoc.Append($"<Cuit>{a.Cuit}</Cuit>");
                        if (!string.IsNullOrEmpty(a.CbteFch)) sbAsoc.Append($"<CbteFch>{a.CbteFch}</CbteFch>");
                        sbAsoc.Append("</CbteAsoc>");
                    }
                    sbAsoc.Append("</CbtesAsoc>");
                }

                string body =
$@"<FECAESolicitar xmlns=""{WsfeNamespace}"">
  <Auth>
    <Token>{_token}</Token>
    <Sign>{_sign}</Sign>
    <Cuit>{_cuitAuth}</Cuit>
  </Auth>
  <FeCAEReq>
    <FeCabReq>
      <CantReg>1</CantReg>
      <PtoVta>{ptoVta}</PtoVta>
      <CbteTipo>{tipoComp}</CbteTipo>
    </FeCabReq>
    <FeDetReq>
      <FECAEDetRequest>
        <Concepto>{det.Concepto}</Concepto>
        <DocTipo>{det.DocTipo}</DocTipo>
        <DocNro>{det.DocNro}</DocNro>
        <CbteDesde>{det.CbteDesde}</CbteDesde>
        <CbteHasta>{det.CbteHasta}</CbteHasta>
        <CbteFch>{det.CbteFch}</CbteFch>
        <ImpTotal>{Fmt(det.ImpTotal)}</ImpTotal>
        <ImpTotConc>{Fmt(det.ImpTotConc)}</ImpTotConc>
        <ImpNeto>{Fmt(det.ImpNeto)}</ImpNeto>
        <ImpOpEx>{Fmt(det.ImpOpEx)}</ImpOpEx>
        <ImpTrib>0.00</ImpTrib>
        <ImpIVA>{Fmt(impIVA)}</ImpIVA>
        {fechasServicio}
        <MonId>{det.MonId}</MonId>
        {monCotiz}
        <CondicionIVAReceptorId>{det.CondicionIVAReceptorId}</CondicionIVAReceptorId>
        {sbAsoc}
        {sbIva}
      </FECAEDetRequest>
    </FeDetReq>
  </FeCAEReq>
</FECAESolicitar>";

                var doc = Llamar("FECAESolicitar", body);

                // Errores generales del webservice (ej. token vencido, CUIT no autorizado, etc.)
                var erroresGenerales = ExtraerErrores(doc);

                var detResp = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "FECAEDetResponse");
                if (detResp == null)
                {
                    ErrorDesc = erroresGenerales.Count > 0
                        ? string.Join(" | ", erroresGenerales)
                        : "La respuesta de ARCA no incluyó FECAEDetResponse: " + doc;
                    return false;
                }

                _ultimaRespuesta = new FeDetRespData
                {
                    Resultado = GetVal(detResp, "Resultado"),
                    CAE = GetVal(detResp, "CAE"),
                    CAEFchVto = GetVal(detResp, "CAEFchVto"),
                    Observaciones = detResp.Descendants()
                        .Where(e => e.Name.LocalName == "Obs")
                        .Select(o => $"{GetVal(o, "Code")}: {GetVal(o, "Msg")}")
                        .ToList()
                };

                if (erroresGenerales.Count > 0)
                    ErrorDesc = string.Join(" | ", erroresGenerales);

                // "A" = Aprobado, "R" = Rechazado, "O" = Observado (pero con reglas actuales de
                // CondicionIVAReceptorId, un valor inválido rechaza el CAE directamente: error 10242/10244)
                return _ultimaRespuesta.Resultado == "A";
            }
            catch (Exception ex)
            {
                ErrorDesc = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Misma firma que la versión del DLL de terceros. El índice se ignora (sólo se procesa
        /// un comprobante por vez) y se deja por compatibilidad con el código existente.
        /// </summary>
        public void AutorizarRespuesta(int indice, out string CAE, out string CAEVencimiento,
            out string resultadoAFIP, out bool reprocesoAFIP)
        {
            if (_ultimaRespuesta == null)
                throw new InvalidOperationException("Llamá primero a Autorizar().");

            CAE = _ultimaRespuesta.CAE;
            CAEVencimiento = _ultimaRespuesta.CAEFchVto;
            resultadoAFIP = _ultimaRespuesta.Resultado;
            reprocesoAFIP = false; // WSFEv1 (a diferencia de WSFEXv1/CAEA) no informa reproceso
        }

        public string AutorizarRespuestaObs(int indice)
        {
            if (_ultimaRespuesta == null) return ErrorDesc ?? "";
            return _ultimaRespuesta.Observaciones.Count > 0
                ? string.Join(Environment.NewLine, _ultimaRespuesta.Observaciones)
                : (ErrorDesc ?? "Comprobante rechazado sin observaciones detalladas.");
        }

        // ---------------------------------------------------------------
        // Helpers privados
        // ---------------------------------------------------------------

        private XDocument Llamar(string operacion, string bodyXml)
        {
            string envelope =
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    {bodyXml}
  </soap:Body>
</soap:Envelope>";

            var req = (HttpWebRequest)WebRequest.Create(URL);
            req.Method = "POST";
            req.ContentType = "text/xml; charset=utf-8";
            req.Headers.Add("SOAPAction", $"\"{WsfeNamespace}{operacion}\"");

            byte[] bytes = Encoding.UTF8.GetBytes(envelope);
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream())
                s.Write(bytes, 0, bytes.Length);

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    return XDocument.Parse(reader.ReadToEnd());
                }
            }
            catch (WebException wex) when (wex.Response != null)
            {
                using (var reader = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    var doc = XDocument.Parse(body);
                    var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring");
                    throw new Exception(fault != null ? fault.Value : body, wex);
                }
            }
        }

        private static List<string> ExtraerErrores(XDocument doc)
        {
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "Err")
                .Select(er => $"{GetVal(er, "Code")}: {GetVal(er, "Msg")}")
                .ToList();
        }

        private static string GetVal(XElement parent, string localName)
        {
            var el = parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
            return el?.Value ?? "";
        }

        private static string Fmt(double valor, int decimales = 2)
        {
            return valor.ToString("F" + decimales, CultureInfo.InvariantCulture);
        }

        private class FeDetReqData
        {
            public int Concepto;
            public int DocTipo;
            public long DocNro;
            public long CbteDesde;
            public long CbteHasta;
            public string CbteFch;
            public double ImpTotal;
            public double ImpTotConc;
            public double ImpNeto;
            public double ImpOpEx;
            public string FchServDesde;
            public string FchServHasta;
            public string FchVtoPago;
            public string MonId;
            public double MonCotiz;
            public int CondicionIVAReceptorId;
            public List<AlicIvaData> Ivas;
            public List<CbteAsocData> CbtesAsoc;
        }

        private class CbteAsocData
        {
            public int Tipo;
            public int PtoVta;
            public long Nro;
            public long Cuit;
            public string CbteFch;
        }

        private class AlicIvaData
        {
            public int Id;
            public double BaseImp;
            public double Importe;
        }

        private class FeDetRespData
        {
            public string Resultado;
            public string CAE;
            public string CAEFchVto;
            public List<string> Observaciones;
        }
    }

    /// <summary>
    /// Tabla de referencia de CondicionIVAReceptorId (RG ARCA 5616/2024).
    /// Los valores válidos dependen del tipo de comprobante que estás emitiendo;
    /// para la lista oficial y actualizada convine llamar a FEParamGetCondicionIvaReceptor.
    /// </summary>
    public static class WsfeCondicionIvaReceptor
    {
        // Válidos para Factura/ND/NC A, M o C (emisor Responsable Inscripto / Monotributista, receptor "inscripto")
        public const int IVAResponsableInscripto = 1;
        public const int ResponsableMonotributo = 6;
        public const int MonotributistaSocial = 13;
        public const int MonotributoTrabajadorIndependientePromovido = 16;

        // Válidos para Factura/ND/NC B o C (receptor consumidor final / exento / etc.)
        public const int IVASujetoExento = 4;
        public const int ConsumidorFinal = 5;
        public const int SujetoNoCategorizado = 7;
        public const int ProveedorDelExterior = 8;
        public const int ClienteDelExterior = 9;
        public const int IVALiberadoLey19640 = 10;
        public const int IVANoAlcanzado = 15;
    }
}

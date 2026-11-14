using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace APC.Arca
{
    /// <summary>Datos fiscales de una empresa para hablar con ARCA (WSAA + WSFEv1).</summary>
    public class ArcaConfig
    {
        public long Cuit { get; set; }
        public int PuntoVenta { get; set; }
        public string CertificadoPath { get; set; }
        public string ClavePath { get; set; }
        public string UrlWsaa { get; set; }
        public string UrlWsfe { get; set; }
        /// <summary>Carpeta donde se cachea el ticket de acceso (una por CUIT).</summary>
        public string CarpetaCacheTA { get; set; }
        public int Concepto { get; set; } = 1;      // 1 = Productos
        public string Moneda { get; set; } = "PES";
    }

    public class AlicuotaIva
    {
        /// <summary>Código ARCA de alícuota: 3 = 0%, 4 = 10,5%, 5 = 21%, 6 = 27%, 8 = 5%, 9 = 2,5%.</summary>
        public int Id { get; set; }
        public decimal BaseImponible { get; set; }
        public decimal Importe { get; set; }
    }

    public class SolicitudCae
    {
        public int TipoComprobante { get; set; }          // 1 = A, 6 = B, 11 = C
        public int TipoDocReceptor { get; set; }          // 80 CUIT, 96 DNI, 99 Otro
        public long NroDocReceptor { get; set; }
        public int CondicionIvaReceptorId { get; set; }   // ver WsfeCondicionIvaReceptor
        public DateTime Fecha { get; set; } = DateTime.Today;
        public decimal ImporteTotal { get; set; }
        public decimal ImporteNoGravado { get; set; }
        public decimal ImporteNeto { get; set; }
        public decimal ImporteExento { get; set; }
        public List<AlicuotaIva> Alicuotas { get; set; } = new List<AlicuotaIva>();
        /// <summary>Comprobante asociado: obligatorio en notas de crédito (la factura que anulan).</summary>
        public ComprobanteAsociado Asociado { get; set; }
    }

    public class ComprobanteAsociado
    {
        public int Tipo { get; set; }        // código ARCA de la factura: 1, 6 u 11
        public int PuntoVenta { get; set; }
        public long Numero { get; set; }
        public long Cuit { get; set; }
        public DateTime Fecha { get; set; }
    }

    public class RespuestaCae
    {
        public bool Aprobado { get; set; }
        public long Numero { get; set; }
        public string Cae { get; set; }
        public string CaeVencimiento { get; set; }
        public string Mensaje { get; set; }
    }

    /// <summary>
    /// Envoltorio de alto nivel sobre WsaaAuth/Wsfev1Client (el mismo código que usa APC.WinForms).
    /// Serializa las emisiones por CUIT: dos cajas del mismo cliente no pueden pedir el
    /// "último comprobante" a la vez, porque obtendrían el mismo número.
    /// </summary>
    public class ArcaService
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
            new ConcurrentDictionary<string, SemaphoreSlim>();

        public RespuestaCae SolicitarCae(ArcaConfig cfg, SolicitudCae s)
        {
            var key = cfg.Cuit + "-" + cfg.PuntoVenta;
            var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            sem.Wait();
            try
            {
                Directory.CreateDirectory(cfg.CarpetaCacheTA);
                var ws = new Wsfev1Client(cfg.CarpetaCacheTA) { CUIT = cfg.Cuit, URL = cfg.UrlWsfe };

                if (!ws.login(cfg.CertificadoPath, cfg.ClavePath, cfg.UrlWsaa))
                    return Error("No se pudo autenticar con ARCA: " + ws.ErrorDesc);

                if (!ws.SFRecuperaLastCMP(cfg.PuntoVenta, s.TipoComprobante))
                    return Error("ARCA no informó el último comprobante: " + ws.ErrorDesc);

                long numero = ws.SFLastCMP + 1;
                string fecha = s.Fecha.ToString("yyyyMMdd");

                ws.Reset();
                ws.AgregaFactura(cfg.Concepto, s.TipoDocReceptor, s.NroDocReceptor, numero, numero, fecha,
                    R(s.ImporteTotal), R(s.ImporteNoGravado), R(s.ImporteNeto), R(s.ImporteExento),
                    fecha, fecha, fecha, cfg.Moneda, 1, s.CondicionIvaReceptorId);

                if (s.Asociado != null)
                    ws.AgregaCompAsoc(s.Asociado.Tipo, s.Asociado.PuntoVenta, s.Asociado.Numero, s.Asociado.Cuit,
                        s.Asociado.Fecha.ToString("yyyyMMdd"));

                if (s.TipoComprobante != 11 && s.TipoComprobante != 13) // Factura C y Nota de crédito C no discriminan IVA
                    foreach (var a in s.Alicuotas)
                        ws.AgregaIVA(a.Id, R(a.BaseImponible), R(a.Importe));

                if (!ws.Autorizar(cfg.PuntoVenta, s.TipoComprobante))
                    return Error("ARCA rechazó el comprobante: " + ws.AutorizarRespuestaObs(0));

                ws.AutorizarRespuesta(0, out var cae, out var vto, out var resultado, out _);
                if (resultado != "A")
                    return Error("ARCA rechazó el comprobante: " + ws.AutorizarRespuestaObs(0));

                return new RespuestaCae { Aprobado = true, Numero = numero, Cae = cae, CaeVencimiento = vto };
            }
            catch (Exception ex)
            {
                return Error("Error comunicándose con ARCA: " + ex.Message);
            }
            finally
            {
                sem.Release();
            }
        }

        private static double R(decimal v) => Math.Round((double)v, 2);

        private static RespuestaCae Error(string msg) => new RespuestaCae { Aprobado = false, Mensaje = msg };
    }
}

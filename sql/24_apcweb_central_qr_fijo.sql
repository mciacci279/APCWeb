-- apcweb:titulo=QR fijo por caja de Mercado Pago
-- apcweb:destino=central
/* ============================================================
   APC Web - 24 - QR fijo (impreso) por caja de Mercado Pago
   Base: APCWeb_Central
   Idempotente.

   Con el QR fijo el cliente escanea el mismo sticker siempre y
   la billetera le muestra el importe de la venta que el cajero
   acaba de cargar. Es como funciona el surtidor de YPF.
   ============================================================ */

IF COL_LENGTH('dbo.MercadoPagoCaja', 'UsaQrFijo') IS NULL
    ALTER TABLE dbo.MercadoPagoCaja ADD UsaQrFijo BIT NOT NULL CONSTRAINT DF_MercadoPagoCaja_UsaQrFijo DEFAULT (0);
GO

PRINT 'Script 24 listo: QR fijo por caja.';

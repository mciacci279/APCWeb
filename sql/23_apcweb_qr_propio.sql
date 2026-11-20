-- apcweb:titulo=QR propio del comercio
-- apcweb:destino=empresa
/* ============================================================
   APC Web - 23 - Cobro con el QR del comercio (interoperable)
   Base: la de CADA EMPRESA (no la central)
   Idempotente: se puede correr las veces que haga falta.

   Guarda el QR interoperable que el comercio saca de su banco
   o de MODO, uno por sucursal. La plata va directo a su cuenta:
   APC Web solo lo muestra en pantalla y el cajero confirma.
   ============================================================ */

IF COL_LENGTH('dbo.Sucursal', 'QrPago') IS NULL
    ALTER TABLE dbo.Sucursal ADD QrPago VARBINARY(MAX) NULL;
GO

IF COL_LENGTH('dbo.Sucursal', 'QrPagoTipo') IS NULL
    ALTER TABLE dbo.Sucursal ADD QrPagoTipo VARCHAR(40) NULL;
GO

/* Alias o CVU, para mostrarlo abajo del QR por si el cliente prefiere escribirlo */
IF COL_LENGTH('dbo.Sucursal', 'QrPagoAlias') IS NULL
    ALTER TABLE dbo.Sucursal ADD QrPagoAlias NVARCHAR(100) NULL;
GO

IF COL_LENGTH('dbo.FormaPago', 'EsQrPropio') IS NULL
    ALTER TABLE dbo.FormaPago ADD EsQrPropio BIT NOT NULL CONSTRAINT DF_FormaPago_EsQrPropio DEFAULT (0);
GO

PRINT 'Script 23 listo: QR propio por sucursal.';

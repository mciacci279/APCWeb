-- apcweb:titulo=Varias cajas de Mercado Pago por sucursal
-- apcweb:destino=central
/* ============================================================
   Factu360 - 26 - Varias cajas de Mercado Pago por sucursal
   Base: APCWeb_Central
   Idempotente: se puede correr las veces que haga falta.
   Hasta ahora habia una sola caja (POS) por punto de venta.
   Con esto cada sucursal puede tener la Caja 1, la Caja 2, etc.,
   cada una con su propio QR. Las cajas que ya existian quedan
   como la Caja 1 de su sucursal.
   ============================================================ */
IF COL_LENGTH('dbo.MercadoPagoCaja', 'Numero') IS NULL
BEGIN
    ALTER TABLE dbo.MercadoPagoCaja ADD Numero INT NOT NULL CONSTRAINT DF_MercadoPagoCaja_Numero DEFAULT (1);
    PRINT 'MercadoPagoCaja.Numero agregada.';
END
GO

IF COL_LENGTH('dbo.MercadoPagoCaja', 'Nombre') IS NULL
BEGIN
    ALTER TABLE dbo.MercadoPagoCaja ADD Nombre NVARCHAR(60) NULL;
    PRINT 'MercadoPagoCaja.Nombre agregada.';
END
GO

/* El indice unico pasa de (empresa, punto de venta) a (empresa, punto de venta, numero de caja) */
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_MercadoPagoCaja_Empresa_Pv' AND object_id = OBJECT_ID('dbo.MercadoPagoCaja'))
BEGIN
    DROP INDEX UX_MercadoPagoCaja_Empresa_Pv ON dbo.MercadoPagoCaja;
    PRINT 'Indice viejo de una caja por punto de venta quitado.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_MercadoPagoCaja_Empresa_Pv_Numero' AND object_id = OBJECT_ID('dbo.MercadoPagoCaja'))
BEGIN
    CREATE UNIQUE INDEX UX_MercadoPagoCaja_Empresa_Pv_Numero ON dbo.MercadoPagoCaja (EmpresaId, PuntoVenta, Numero);
    PRINT 'Indice por punto de venta y numero de caja creado.';
END
GO

PRINT 'Script 26 listo: varias cajas de Mercado Pago por sucursal.';

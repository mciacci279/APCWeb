-- apcweb:titulo=Cobros con QR
-- apcweb:destino=empresa
/* ============================================================
   APC Web - 22 - Cobros con QR de Mercado Pago
   Base: la de CADA EMPRESA (no la central)
   Idempotente.
   ============================================================ */

IF OBJECT_ID(N'dbo.PagoQr', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PagoQr
    (
        Id          UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_PagoQr PRIMARY KEY,
        Fecha       DATETIME         NOT NULL CONSTRAINT DF_PagoQr_Fecha DEFAULT (GETDATE()),
        SucursalId  UNIQUEIDENTIFIER NULL,
        UsuarioId   UNIQUEIDENTIFIER NULL,
        Importe     DECIMAL(18, 2)   NOT NULL,
        /* external_reference que se le manda a Mercado Pago; unico por cobro */
        Referencia  NVARCHAR(64)     NOT NULL,
        OrderId     NVARCHAR(40)     NULL,
        PagoId      NVARCHAR(40)     NULL,
        /* CREADA | PAGADA | CANCELADA | VENCIDA | ERROR */
        Estado      VARCHAR(12)      NOT NULL CONSTRAINT DF_PagoQr_Estado DEFAULT ('CREADA'),
        Detalle     NVARCHAR(300)    NULL,
        QrData      NVARCHAR(900)    NULL,
        FacturaId   UNIQUEIDENTIFIER NULL,
        FechaPago   DATETIME         NULL
    );
    CREATE UNIQUE INDEX UX_PagoQr_Referencia ON dbo.PagoQr (Referencia);
    CREATE INDEX IX_PagoQr_Fecha ON dbo.PagoQr (Fecha);
    PRINT 'Tabla PagoQr creada.';
END
ELSE
    PRINT 'Tabla PagoQr ya existia.';
GO

/* La forma de pago que cobra con el QR de Mercado Pago */
IF COL_LENGTH('dbo.FormaPago', 'EsMercadoPago') IS NULL
BEGIN
    ALTER TABLE dbo.FormaPago
        ADD EsMercadoPago BIT NOT NULL CONSTRAINT DF_FormaPago_EsMp DEFAULT (0);
    PRINT 'FormaPago.EsMercadoPago agregada.';
END
ELSE
    PRINT 'FormaPago.EsMercadoPago ya existia.';
GO

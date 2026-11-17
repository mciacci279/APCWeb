/* ============================================================
   APC Web - 18 - Tipos de precio y su forma de pago
   Base: la de CADA EMPRESA (no la central)
   Idempotente.

   Un tipo de precio es un nombre + un ajuste en % sobre el precio de
   lista (el de la gondola): positivo recarga, negativo descuenta.
   Cada forma de pago puede apuntar a uno y el sistema lo aplica solo.
   ============================================================ */

IF OBJECT_ID(N'dbo.TipoPrecio', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TipoPrecio
    (
        Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_TipoPrecio PRIMARY KEY,
        Nombre  NVARCHAR(60)     NOT NULL,
        /* % sobre el precio de lista: 10 = recargo del 10%, -10 = descuento del 10% */
        Ajuste  DECIMAL(9,4)     NOT NULL CONSTRAINT DF_TipoPrecio_Ajuste DEFAULT (0),
        Activo  BIT              NOT NULL CONSTRAINT DF_TipoPrecio_Activo DEFAULT (1),
        Orden   INT              NOT NULL CONSTRAINT DF_TipoPrecio_Orden  DEFAULT (0)
    );
    CREATE UNIQUE INDEX UX_TipoPrecio_Nombre ON dbo.TipoPrecio (Nombre);
    PRINT 'Tabla TipoPrecio creada.';
END
ELSE
    PRINT 'Tabla TipoPrecio ya existia.';
GO

/* "Precio de lista" siempre existe y no ajusta nada: es el de la gondola. */
IF NOT EXISTS (SELECT 1 FROM dbo.TipoPrecio WHERE Nombre = N'Precio de lista')
    INSERT INTO dbo.TipoPrecio (Id, Nombre, Ajuste, Activo, Orden)
    VALUES (NEWID(), N'Precio de lista', 0, 1, 0);
GO

IF COL_LENGTH('dbo.FormaPago', 'TipoPrecioId') IS NULL
BEGIN
    ALTER TABLE dbo.FormaPago ADD TipoPrecioId UNIQUEIDENTIFIER NULL;
    PRINT 'FormaPago.TipoPrecioId agregada.';
END
ELSE
    PRINT 'FormaPago.TipoPrecioId ya existia.';
GO

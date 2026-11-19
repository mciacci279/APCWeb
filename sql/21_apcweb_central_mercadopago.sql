/* ============================================================
   APC Web - 21 - Mercado Pago: cuentas y cajas (QR)
   Base: APCWeb_Central
   Idempotente.

   Una cuenta de Mercado Pago por empresa (el access token de SU cuenta)
   y una caja (POS) por sucursal, que es contra la que se crea cada QR.
   ============================================================ */

IF OBJECT_ID(N'dbo.MercadoPagoCuenta', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MercadoPagoCuenta
    (
        EmpresaId    UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_MercadoPagoCuenta PRIMARY KEY,
        /* Access token de la aplicacion del comercio. APP_USR... (produccion) o TEST... */
        AccessToken  NVARCHAR(400)    NOT NULL,
        Ambiente     VARCHAR(4)       NOT NULL CONSTRAINT DF_MpCuenta_Ambiente DEFAULT ('TEST'),
        UserId       NVARCHAR(40)     NULL,   /* collector id, lo devuelve /users/me */
        StoreId      NVARCHAR(40)     NULL,   /* tienda creada por API */
        Apodo        NVARCHAR(80)     NULL,   /* nickname de la cuenta, para mostrar */
        Activo       BIT              NOT NULL CONSTRAINT DF_MpCuenta_Activo DEFAULT (1),
        FechaAlta    DATETIME         NOT NULL CONSTRAINT DF_MpCuenta_Fecha DEFAULT (GETDATE())
    );
    PRINT 'Tabla MercadoPagoCuenta creada.';
END
ELSE
    PRINT 'Tabla MercadoPagoCuenta ya existia.';
GO

IF OBJECT_ID(N'dbo.MercadoPagoCaja', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MercadoPagoCaja
    (
        Id            UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_MercadoPagoCaja PRIMARY KEY,
        EmpresaId     UNIQUEIDENTIFIER NOT NULL,
        /* Id de la sucursal en la base de la empresa (puede ser NULL si todavia no se mapeo) */
        SucursalId    UNIQUEIDENTIFIER NULL,
        PuntoVenta    INT              NOT NULL,
        /* El que viaja en la order: lo armamos como <codigo empresa>-<pto vta>, sin guiones raros */
        ExternalPosId NVARCHAR(40)     NOT NULL,
        PosId         NVARCHAR(40)     NULL,
        QrEstatico    NVARCHAR(900)    NULL,
        FechaAlta     DATETIME         NOT NULL CONSTRAINT DF_MpCaja_Fecha DEFAULT (GETDATE())
    );
    CREATE UNIQUE INDEX UX_MercadoPagoCaja_Empresa_Pv ON dbo.MercadoPagoCaja (EmpresaId, PuntoVenta);
    PRINT 'Tabla MercadoPagoCaja creada.';
END
ELSE
    PRINT 'Tabla MercadoPagoCaja ya existia.';
GO

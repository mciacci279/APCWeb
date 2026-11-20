-- apcweb:titulo=Sucursales
-- apcweb:destino=empresa
/* APC Web — sucursales y stock por sucursal.
   Ejecutar UNA vez en la base de CADA empresa (SuperMarketDB_…).
   No modifica ninguna tabla existente: APC.WinForms sigue funcionando igual,
   porque Producto.Stock se mantiene como el stock total de todas las sucursales. */

IF OBJECT_ID(N'dbo.Sucursal', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Sucursal
    (
        Id            UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Sucursal PRIMARY KEY,
        Nombre        NVARCHAR(100)    NOT NULL,
        PuntoVenta    INT              NOT NULL,
        Domicilio     NVARCHAR(150)    NULL,
        Localidad     NVARCHAR(100)    NULL,
        Telefono      NVARCHAR(50)     NULL,
        LeyendaTicket NVARCHAR(200)    NULL,
        Activa        BIT              NOT NULL CONSTRAINT DF_Sucursal_Activa DEFAULT (1),
        FechaAlta     DATETIME         NOT NULL CONSTRAINT DF_Sucursal_FechaAlta DEFAULT (GETDATE())
    );

    CREATE UNIQUE INDEX UX_Sucursal_PuntoVenta ON dbo.Sucursal (PuntoVenta);
END
GO

IF OBJECT_ID(N'dbo.StockSucursal', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockSucursal
    (
        SucursalId  UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_StockSucursal_Sucursal REFERENCES dbo.Sucursal(Id),
        ProductoId  UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_StockSucursal_Producto REFERENCES dbo.Producto(Id),
        Stock       DECIMAL(18, 3)   NOT NULL CONSTRAINT DF_StockSucursal_Stock DEFAULT (0),
        StockMinimo DECIMAL(18, 3)   NULL,
        CONSTRAINT PK_StockSucursal PRIMARY KEY (SucursalId, ProductoId)
    );
END
GO

/* Ajustes manuales y transferencias entre sucursales. Las ventas no se registran acá:
   ya quedan en Factura / Detalle. */
IF OBJECT_ID(N'dbo.MovimientoStock', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MovimientoStock
    (
        Id            UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_MovimientoStock PRIMARY KEY,
        Fecha         DATETIME         NOT NULL CONSTRAINT DF_MovimientoStock_Fecha DEFAULT (GETDATE()),
        SucursalId    UNIQUEIDENTIFIER NOT NULL,
        ProductoId    UNIQUEIDENTIFIER NOT NULL,
        Cantidad      DECIMAL(18, 3)   NOT NULL,
        Tipo          NVARCHAR(30)     NOT NULL,
        UsuarioId     UNIQUEIDENTIFIER NULL,
        Referencia    NVARCHAR(100)    NULL,
        Observaciones NVARCHAR(300)    NULL
    );

    CREATE INDEX IX_MovimientoStock_Fecha ON dbo.MovimientoStock (Fecha);
    CREATE INDEX IX_MovimientoStock_Producto ON dbo.MovimientoStock (ProductoId, SucursalId);
END
GO

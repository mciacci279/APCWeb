-- apcweb:titulo=Cajas por sucursal y quien esta en cada una
-- apcweb:destino=empresa
/* ============================================================
   Factu360 - 27 - Cajas por sucursal y sesiones de caja
   Base: la de CADA EMPRESA (no la central)
   Idempotente: se puede correr las veces que haga falta.

   PuestoCaja: las cajas de cada sucursal (Caja 1, Caja 2...).
     Se llama "Puesto" para no chocar con tablas del sistema de
     escritorio. Puede apuntar a su caja de Mercado Pago.
   PuestoCajaSesion: quien trabaja en cada caja y desde cuando.
     El indice UX_PuestoCajaSesion_Abierta impide que dos usuarios
     tengan abierta la misma caja al mismo tiempo.
   ============================================================ */
IF OBJECT_ID(N'dbo.PuestoCaja', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PuestoCaja
    (
        Id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_PuestoCaja PRIMARY KEY,
        SucursalId        UNIQUEIDENTIFIER NOT NULL,
        Numero            INT              NOT NULL,
        Nombre            NVARCHAR(60)     NULL,
        /* Id de la caja de Mercado Pago en la base central (MercadoPagoCaja.Id) */
        MercadoPagoCajaId UNIQUEIDENTIFIER NULL,
        Activa            BIT              NOT NULL CONSTRAINT DF_PuestoCaja_Activa DEFAULT (1),
        FechaAlta         DATETIME         NOT NULL CONSTRAINT DF_PuestoCaja_FechaAlta DEFAULT (GETDATE())
    );
    CREATE UNIQUE INDEX UX_PuestoCaja_Sucursal_Numero ON dbo.PuestoCaja (SucursalId, Numero);
    PRINT 'Tabla PuestoCaja creada.';
END
ELSE
    PRINT 'Tabla PuestoCaja ya existia.';
GO

IF OBJECT_ID(N'dbo.PuestoCajaSesion', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PuestoCajaSesion
    (
        Id           UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_PuestoCajaSesion PRIMARY KEY,
        CajaId       UNIQUEIDENTIFIER NOT NULL,
        UsuarioId    UNIQUEIDENTIFIER NOT NULL,
        FechaIngreso DATETIME         NOT NULL CONSTRAINT DF_PuestoCajaSesion_Ingreso DEFAULT (GETDATE()),
        /* La pantalla avisa cada minuto que sigue abierta; sin aviso por 10 minutos, la caja se libera */
        UltimoLatido DATETIME         NOT NULL CONSTRAINT DF_PuestoCajaSesion_Latido DEFAULT (GETDATE()),
        FechaSalida  DATETIME         NULL,
        /* SALIO | INACTIVIDAD | LIBERADA | CAMBIO */
        Motivo       VARCHAR(12)      NULL,
        /* Quien la libero, si fue un supervisor o administrador */
        LiberadaPor  UNIQUEIDENTIFIER NULL
    );
    CREATE INDEX IX_PuestoCajaSesion_Usuario ON dbo.PuestoCajaSesion (UsuarioId, FechaIngreso);
    CREATE INDEX IX_PuestoCajaSesion_Ingreso ON dbo.PuestoCajaSesion (FechaIngreso);
    PRINT 'Tabla PuestoCajaSesion creada.';
END
ELSE
    PRINT 'Tabla PuestoCajaSesion ya existia.';
GO

/* Una sola sesion abierta por caja: lo garantiza la base, no solo la pantalla */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PuestoCajaSesion_Abierta' AND object_id = OBJECT_ID('dbo.PuestoCajaSesion'))
BEGIN
    CREATE UNIQUE INDEX UX_PuestoCajaSesion_Abierta ON dbo.PuestoCajaSesion (CajaId) WHERE FechaSalida IS NULL;
    PRINT 'Indice de una sesion abierta por caja creado.';
END
GO

PRINT 'Script 27 listo: cajas por sucursal y sesiones de caja.';

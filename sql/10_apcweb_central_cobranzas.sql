/* APC Web — cobranzas de CM Tech: el abono mensual que se le cobra a cada empresa.
   Ejecutar UNA vez en APCWeb_Central. Se puede correr más de una vez sin problema.
   No tiene nada que ver con la facturación de los clientes: es el control de cobro propio. */
USE APCWeb_Central;
GO

/* ---- Datos de contacto de cada empresa, para avisarle del cobro ---- */
IF COL_LENGTH(N'dbo.Empresa', N'ContactoNombre') IS NULL
    ALTER TABLE dbo.Empresa ADD ContactoNombre NVARCHAR(100) NULL;
GO
IF COL_LENGTH(N'dbo.Empresa', N'ContactoTelefono') IS NULL
    ALTER TABLE dbo.Empresa ADD ContactoTelefono NVARCHAR(50) NULL;
GO
IF COL_LENGTH(N'dbo.Empresa', N'ContactoEmail') IS NULL
    ALTER TABLE dbo.Empresa ADD ContactoEmail NVARCHAR(100) NULL;
GO
IF COL_LENGTH(N'dbo.Empresa', N'FacturadorId') IS NULL
    ALTER TABLE dbo.Empresa ADD FacturadorId UNIQUEIDENTIFIER NULL;
GO

/* ---- Quién factura: cada integrante de CM Tech, con sus datos de cobro ---- */
IF OBJECT_ID(N'dbo.Facturador', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Facturador
    (
        Id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Facturador PRIMARY KEY,
        Nombre  NVARCHAR(100)    NOT NULL,
        Cuit    NVARCHAR(13)     NULL,
        Alias   NVARCHAR(60)     NULL,
        Cbu     NVARCHAR(30)     NULL,
        Banco   NVARCHAR(60)     NULL,
        Activo  BIT              NOT NULL CONSTRAINT DF_Facturador_Activo DEFAULT (1)
    );
END
GO

/* ---- Un cobro por empresa y por mes ---- */
IF OBJECT_ID(N'dbo.Cobro', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Cobro
    (
        Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Cobro PRIMARY KEY,
        EmpresaId      UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_Cobro_Empresa REFERENCES dbo.Empresa(Id),
        Periodo        CHAR(7)          NOT NULL,              -- 'aaaa-mm'
        Concepto       NVARCHAR(150)    NULL,
        Monto          DECIMAL(18, 2)   NOT NULL,
        FacturadorId   UNIQUEIDENTIFIER NULL,
        ComprobanteNro NVARCHAR(40)     NULL,                  -- número de la factura que emitió el monotributista
        Estado         VARCHAR(12)      NOT NULL CONSTRAINT DF_Cobro_Estado DEFAULT ('PENDIENTE'),
        FechaAlta      DATETIME         NOT NULL CONSTRAINT DF_Cobro_FechaAlta DEFAULT (GETDATE()),
        CreadoPor      UNIQUEIDENTIFIER NULL,
        FechaEnvio     DATETIME         NULL,
        EnviadoPor     UNIQUEIDENTIFIER NULL,
        FechaPago      DATE             NULL,
        MedioPago      NVARCHAR(40)     NULL,
        CobradoPor     UNIQUEIDENTIFIER NULL,
        Observaciones  NVARCHAR(300)    NULL,
        CONSTRAINT UQ_Cobro_Empresa_Periodo UNIQUE (EmpresaId, Periodo)
    );

    CREATE INDEX IX_Cobro_Periodo ON dbo.Cobro (Periodo, Estado);
END
GO

/* ---- Historial de avisos enviados (WhatsApp, email, teléfono) ---- */
IF OBJECT_ID(N'dbo.CobroEnvio', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CobroEnvio
    (
        Id        UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_CobroEnvio PRIMARY KEY,
        CobroId   UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_CobroEnvio_Cobro REFERENCES dbo.Cobro(Id),
        Fecha     DATETIME         NOT NULL CONSTRAINT DF_CobroEnvio_Fecha DEFAULT (GETDATE()),
        UsuarioId UNIQUEIDENTIFIER NULL,
        Medio     NVARCHAR(20)     NOT NULL,
        Destino   NVARCHAR(100)    NULL,
        Nota      NVARCHAR(200)    NULL
    );

    CREATE INDEX IX_CobroEnvio_Cobro ON dbo.CobroEnvio (CobroId, Fecha);
END
GO

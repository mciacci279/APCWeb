/* APC Web — base central de CM Tech (empresas + usuarios web + registro de ingresos).
   Ejecutar UNA vez en el SQL Server de la VPS, conectado como sa. Compatible con SQL Server 2014.
   Las bases de cada cliente no se modifican con este script. */

IF DB_ID(N'APCWeb_Central') IS NULL
    CREATE DATABASE APCWeb_Central;
GO

USE APCWeb_Central;
GO

IF OBJECT_ID(N'dbo.Empresa', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Empresa
    (
        Id               UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Empresa PRIMARY KEY,
        Codigo           NVARCHAR(30)     NOT NULL CONSTRAINT UQ_Empresa_Codigo UNIQUE,
        Nombre           NVARCHAR(150)    NOT NULL,
        ConnectionString NVARCHAR(500)    NOT NULL,
        PuntoVenta       INT              NOT NULL,
        Ambiente         VARCHAR(4)       NOT NULL CONSTRAINT DF_Empresa_Ambiente DEFAULT ('HOMO')
                                          CONSTRAINT CK_Empresa_Ambiente CHECK (Ambiente IN ('HOMO', 'PROD')),
        CertificadoPath  NVARCHAR(260)    NULL,
        ClavePath        NVARCHAR(260)    NULL,
        Concepto         INT              NOT NULL CONSTRAINT DF_Empresa_Concepto DEFAULT (1),
        Moneda           VARCHAR(3)       NOT NULL CONSTRAINT DF_Empresa_Moneda DEFAULT ('PES'),
        Activa           BIT              NOT NULL CONSTRAINT DF_Empresa_Activa DEFAULT (1),
        MotivoSuspension NVARCHAR(250)    NULL,
        FechaAlta        DATETIME         NOT NULL CONSTRAINT DF_Empresa_FechaAlta DEFAULT (GETDATE())
    );
END
GO

IF COL_LENGTH(N'dbo.Empresa', N'Logo') IS NULL
    ALTER TABLE dbo.Empresa ADD Logo VARBINARY(MAX) NULL, LogoTipo VARCHAR(30) NULL;
GO

IF COL_LENGTH(N'dbo.Empresa', N'LeyendaTicket') IS NULL
    ALTER TABLE dbo.Empresa ADD LeyendaTicket NVARCHAR(200) NULL
        CONSTRAINT DF_Empresa_LeyendaTicket DEFAULT (N'Muchas gracias por su compra!!!') WITH VALUES;
GO

IF OBJECT_ID(N'dbo.Usuario', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Usuario
    (
        Id               UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Usuario PRIMARY KEY,
        EmpresaId        UNIQUEIDENTIFIER NULL CONSTRAINT FK_Usuario_Empresa REFERENCES dbo.Empresa(Id), -- NULL = CM Tech
        NombreUsuario    NVARCHAR(50)     NOT NULL,
        Nombre           NVARCHAR(150)    NULL,
        Apellido         NVARCHAR(150)    NULL,
        Email            NVARCHAR(150)    NULL,
        ClaveHash        NVARCHAR(200)    NOT NULL,   -- PBKDF2-SHA256, nunca la clave en texto
        Rol              VARCHAR(20)      NOT NULL CONSTRAINT CK_Usuario_Rol CHECK (Rol IN ('SuperAdmin', 'Admin', 'Empleado')),
        Activo           BIT              NOT NULL CONSTRAINT DF_Usuario_Activo DEFAULT (1),
        IntentosFallidos INT              NOT NULL CONSTRAINT DF_Usuario_Intentos DEFAULT (0),
        BloqueadoHasta   DATETIME         NULL,
        UltimoIngreso    DATETIME         NULL,
        FechaAlta        DATETIME         NOT NULL CONSTRAINT DF_Usuario_FechaAlta DEFAULT (GETDATE())
    );

    -- Un mismo nombre de usuario puede repetirse en empresas distintas, no dentro de la misma
    CREATE UNIQUE INDEX UX_Usuario_Empresa_Nombre ON dbo.Usuario (EmpresaId, NombreUsuario);
END
GO

IF OBJECT_ID(N'dbo.IngresoLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IngresoLog
    (
        Id            BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_IngresoLog PRIMARY KEY,
        Fecha         DATETIME          NOT NULL CONSTRAINT DF_IngresoLog_Fecha DEFAULT (GETDATE()),
        EmpresaCodigo NVARCHAR(30)      NULL,
        NombreUsuario NVARCHAR(50)      NULL,
        UsuarioId     UNIQUEIDENTIFIER  NULL,
        Exito         BIT               NOT NULL,
        Motivo        NVARCHAR(100)     NULL,
        Ip            NVARCHAR(50)      NULL
    );

    CREATE INDEX IX_IngresoLog_Fecha ON dbo.IngresoLog (Fecha DESC);
END
GO

/* Acceso para el login de la web (si ya lo creaste para las bases de clientes):
USE APCWeb_Central;
CREATE USER apcweb FOR LOGIN apcweb;
ALTER ROLE db_datareader ADD MEMBER apcweb;
ALTER ROLE db_datawriter ADD MEMBER apcweb;
*/

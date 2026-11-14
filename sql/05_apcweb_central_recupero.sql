/* APC Web — recupero de clave por email.
   Ejecutar UNA vez en APCWeb_Central. Solo se guarda el hash del token, nunca el link. */
USE APCWeb_Central;
GO

IF OBJECT_ID(N'dbo.RecuperoClave', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RecuperoClave
    (
        Id        UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RecuperoClave PRIMARY KEY,
        UsuarioId UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_RecuperoClave_Usuario REFERENCES dbo.Usuario(Id),
        TokenHash VARBINARY(32)    NOT NULL,
        Fecha     DATETIME         NOT NULL CONSTRAINT DF_RecuperoClave_Fecha DEFAULT (GETDATE()),
        Vence     DATETIME         NOT NULL,
        Usado     BIT              NOT NULL CONSTRAINT DF_RecuperoClave_Usado DEFAULT (0),
        Ip        NVARCHAR(50)     NULL
    );
    CREATE UNIQUE INDEX UX_RecuperoClave_Token ON dbo.RecuperoClave (TokenHash);
    CREATE INDEX IX_RecuperoClave_Usuario ON dbo.RecuperoClave (UsuarioId, Fecha);
END
GO

IF OBJECT_ID(N'dbo.IngresoLog', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_IngresoLog_Empresa' AND object_id = OBJECT_ID(N'dbo.IngresoLog'))
    CREATE INDEX IX_IngresoLog_Empresa ON dbo.IngresoLog (EmpresaCodigo, Fecha DESC);
GO

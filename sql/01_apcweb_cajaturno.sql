/* APC Web — tabla nueva para apertura/cierre de caja.
   Ejecutar una vez en la base de cada cliente (compatible con SQL Server 2014).
   No modifica ninguna tabla existente: APC.WinForms sigue funcionando igual. */

IF OBJECT_ID(N'dbo.CajaTurno', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CajaTurno
    (
        Id               UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_CajaTurno PRIMARY KEY,
        UsuarioId        UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_CajaTurno_Usuario REFERENCES dbo.Usuario(Id),
        FechaApertura    DATETIME         NOT NULL,
        MontoInicial     DECIMAL(18, 2)   NOT NULL CONSTRAINT DF_CajaTurno_MontoInicial DEFAULT (0),
        FechaCierre      DATETIME         NULL,
        EfectivoEsperado DECIMAL(18, 2)   NULL,
        EfectivoContado  DECIMAL(18, 2)   NULL,
        Diferencia       DECIMAL(18, 2)   NULL,
        Observaciones    NVARCHAR(500)    NULL
    );

    CREATE INDEX IX_CajaTurno_Usuario_Abierto ON dbo.CajaTurno (UsuarioId, FechaCierre);
END
GO

/* APC Web — tablero de trabajo de CM Tech (tipo Trello).
   Ejecutar UNA vez en APCWeb_Central. Se puede correr más de una vez sin problema. */
USE APCWeb_Central;
GO

IF OBJECT_ID(N'dbo.Ticket', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Ticket
    (
        Id            UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Ticket PRIMARY KEY,
        Numero        INT              IDENTITY(1, 1) NOT NULL,
        Titulo        NVARCHAR(150)    NOT NULL,
        Detalle       NVARCHAR(MAX)    NULL,
        Estado        VARCHAR(16)      NOT NULL CONSTRAINT DF_Ticket_Estado DEFAULT ('PENDIENTE'),
        Prioridad     VARCHAR(10)      NOT NULL CONSTRAINT DF_Ticket_Prioridad DEFAULT ('NORMAL'),
        Tipo          VARCHAR(16)      NOT NULL CONSTRAINT DF_Ticket_Tipo DEFAULT ('PEDIDO'),
        EmpresaId     UNIQUEIDENTIFIER NULL CONSTRAINT FK_Ticket_Empresa REFERENCES dbo.Empresa(Id),
        AsignadoA     UNIQUEIDENTIFIER NULL,
        Vencimiento   DATE             NULL,
        Orden         INT              NOT NULL CONSTRAINT DF_Ticket_Orden DEFAULT (0),
        FechaAlta     DATETIME         NOT NULL CONSTRAINT DF_Ticket_FechaAlta DEFAULT (GETDATE()),
        CreadoPor     UNIQUEIDENTIFIER NULL,
        FechaCierre   DATETIME         NULL,
        CerradoPor    UNIQUEIDENTIFIER NULL
    );

    CREATE INDEX IX_Ticket_Estado ON dbo.Ticket (Estado, Orden);
END
GO

/* Comentarios y novedades de cada ticket */
IF OBJECT_ID(N'dbo.TicketNota', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TicketNota
    (
        Id        UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_TicketNota PRIMARY KEY,
        TicketId  UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_TicketNota_Ticket REFERENCES dbo.Ticket(Id),
        Fecha     DATETIME         NOT NULL CONSTRAINT DF_TicketNota_Fecha DEFAULT (GETDATE()),
        UsuarioId UNIQUEIDENTIFIER NULL,
        Texto     NVARCHAR(1000)   NOT NULL
    );

    CREATE INDEX IX_TicketNota_Ticket ON dbo.TicketNota (TicketId, Fecha);
END
GO

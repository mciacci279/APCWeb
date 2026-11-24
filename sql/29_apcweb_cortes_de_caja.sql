-- apcweb:titulo=Cortes de caja al imprimir arqueos parciales
-- apcweb:destino=empresa
/* ============================================================
   Factu360 - 29 - Cortes de caja (arqueos parciales)
   Base: la de CADA EMPRESA (no la central)
   Idempotente: se puede correr las veces que haga falta.

   Cada vez que se imprime un arqueo parcial queda un "corte":
   el próximo parcial muestra lo vendido desde ese corte, además
   del acumulado desde el inicio del día (o desde la apertura).
   Lote: una misma impresión (varias cajas juntas comparten lote;
     reimprimir la misma página no agrega otro corte).
   PuestoCajaId: el corte es de una caja de la sucursal.
   UsuarioId: o de un usuario (turno por usuario o "sin caja").
   ============================================================ */
IF OBJECT_ID('dbo.CajaCorte', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CajaCorte (
        Id           UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_CajaCorte PRIMARY KEY DEFAULT NEWID(),
        Lote         UNIQUEIDENTIFIER NOT NULL,
        PuestoCajaId UNIQUEIDENTIFIER NULL,
        UsuarioId    UNIQUEIDENTIFIER NULL,
        Fecha        DATETIME NOT NULL CONSTRAINT DF_CajaCorte_Fecha DEFAULT GETDATE(),
        ImpresoPor   UNIQUEIDENTIFIER NULL
    );
    PRINT 'Tabla CajaCorte creada.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CajaCorte_Lote' AND object_id = OBJECT_ID('dbo.CajaCorte'))
    CREATE UNIQUE INDEX UX_CajaCorte_Lote ON dbo.CajaCorte (Lote, PuestoCajaId, UsuarioId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CajaCorte_Caja' AND object_id = OBJECT_ID('dbo.CajaCorte'))
    CREATE INDEX IX_CajaCorte_Caja ON dbo.CajaCorte (PuestoCajaId, UsuarioId, Fecha);
GO

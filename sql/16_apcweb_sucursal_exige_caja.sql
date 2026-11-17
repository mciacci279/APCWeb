/* ============================================================
   APC Web - 16 - Apertura de caja configurable por sucursal
   Base: la de CADA EMPRESA (no la central)
   Idempotente: se puede correr las veces que haga falta.
   ============================================================ */

IF COL_LENGTH('dbo.Sucursal', 'ExigeCaja') IS NULL
BEGIN
    ALTER TABLE dbo.Sucursal
        ADD ExigeCaja BIT NOT NULL CONSTRAINT DF_Sucursal_ExigeCaja DEFAULT (1);
    PRINT 'Sucursal.ExigeCaja agregada (todas las sucursales arrancan exigiendo apertura de caja).';
END
ELSE
    PRINT 'Sucursal.ExigeCaja ya existia.';
GO

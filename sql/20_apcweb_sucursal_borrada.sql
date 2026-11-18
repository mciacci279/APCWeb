/* ============================================================
   APC Web - 20 - Borrado logico de sucursales
   Base: la de CADA EMPRESA (no la central)
   Idempotente.

   Borrada = 1: la sucursal desaparece del sistema pero la fila queda,
   junto con su stock y sus movimientos, por si hay que recuperarla.
   ============================================================ */

IF COL_LENGTH('dbo.Sucursal', 'Borrada') IS NULL
BEGIN
    ALTER TABLE dbo.Sucursal
        ADD Borrada BIT NOT NULL CONSTRAINT DF_Sucursal_Borrada DEFAULT (0);
    PRINT 'Sucursal.Borrada agregada.';
END
ELSE
    PRINT 'Sucursal.Borrada ya existia.';
GO

-- apcweb:titulo=Caja por sucursal
-- apcweb:destino=empresa
/* APC Web — la caja queda asociada a la sucursal donde se abrió,
   para que el supervisor vea las cajas de su sucursal.
   Ejecutar UNA vez en la base de CADA empresa. */

IF COL_LENGTH(N'dbo.CajaTurno', N'SucursalId') IS NULL
    ALTER TABLE dbo.CajaTurno ADD SucursalId UNIQUEIDENTIFIER NULL;
GO

/* Las cajas abiertas antes de este cambio quedan en la primera sucursal. */
IF OBJECT_ID(N'dbo.Sucursal', N'U') IS NOT NULL
    UPDATE ct SET ct.SucursalId = (SELECT TOP 1 Id FROM dbo.Sucursal ORDER BY PuntoVenta)
    FROM dbo.CajaTurno ct WHERE ct.SucursalId IS NULL;
GO

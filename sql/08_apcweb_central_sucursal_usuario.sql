-- apcweb:titulo=Sucursal por usuario
-- apcweb:destino=central
/* APC Web — cada usuario trabaja en una sucursal.
   Ejecutar UNA vez en APCWeb_Central.
   Los usuarios que queden sin sucursal usan la casa central. */
USE APCWeb_Central;
GO

IF COL_LENGTH(N'dbo.Usuario', N'SucursalId') IS NULL
    ALTER TABLE dbo.Usuario ADD SucursalId UNIQUEIDENTIFIER NULL;
GO

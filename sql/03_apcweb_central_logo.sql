-- apcweb:titulo=Logo de la empresa
-- apcweb:destino=central
/* APC Web — logo de la empresa para las facturas.
   Ejecutar UNA vez en APCWeb_Central (si la base se creó con el script 02 antes de este cambio). */
USE APCWeb_Central;
GO

IF COL_LENGTH(N'dbo.Empresa', N'Logo') IS NULL
    ALTER TABLE dbo.Empresa ADD Logo VARBINARY(MAX) NULL;
GO

IF COL_LENGTH(N'dbo.Empresa', N'LogoTipo') IS NULL
    ALTER TABLE dbo.Empresa ADD LogoTipo VARCHAR(30) NULL;
GO

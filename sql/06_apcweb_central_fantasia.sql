/* APC Web — nombre de fantasía del negocio (la columna Nombre pasa a mostrarse como "Razón social").
   Ejecutar UNA vez en APCWeb_Central. */
USE APCWeb_Central;
GO

IF COL_LENGTH(N'dbo.Empresa', N'NombreFantasia') IS NULL
    ALTER TABLE dbo.Empresa ADD NombreFantasia NVARCHAR(150) NULL;
GO

-- apcweb:titulo=Abono y doble factor
-- apcweb:destino=central
/* APC Web — control de abono por empresa y doble factor para los usuarios.
   Ejecutar UNA vez en APCWeb_Central. Se puede correr más de una vez sin problema. */
USE APCWeb_Central;
GO

IF COL_LENGTH(N'dbo.Empresa', N'AbonoPagoHasta') IS NULL
    ALTER TABLE dbo.Empresa ADD AbonoPagoHasta DATE NULL;
GO
IF COL_LENGTH(N'dbo.Empresa', N'AbonoDiasGracia') IS NULL
    ALTER TABLE dbo.Empresa ADD AbonoDiasGracia INT NOT NULL
        CONSTRAINT DF_Empresa_AbonoDiasGracia DEFAULT (10) WITH VALUES;
GO
IF COL_LENGTH(N'dbo.Empresa', N'AbonoMonto') IS NULL
    ALTER TABLE dbo.Empresa ADD AbonoMonto DECIMAL(18, 2) NULL;
GO
IF COL_LENGTH(N'dbo.Empresa', N'AbonoNotas') IS NULL
    ALTER TABLE dbo.Empresa ADD AbonoNotas NVARCHAR(300) NULL;
GO

IF COL_LENGTH(N'dbo.Usuario', N'TotpSecreto') IS NULL
    ALTER TABLE dbo.Usuario ADD TotpSecreto NVARCHAR(64) NULL;
GO
IF COL_LENGTH(N'dbo.Usuario', N'TotpActivo') IS NULL
    ALTER TABLE dbo.Usuario ADD TotpActivo BIT NOT NULL
        CONSTRAINT DF_Usuario_TotpActivo DEFAULT (0) WITH VALUES;
GO

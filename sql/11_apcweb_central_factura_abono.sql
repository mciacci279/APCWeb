/* APC Web — facturación propia de CM Tech: cada facturador monotributista emite su Factura C
   por el abono, con CAE de ARCA. Ejecutar UNA vez en APCWeb_Central, después del script 10. */
USE APCWeb_Central;
GO

/* ---- Datos fiscales del facturador (para pedir el CAE y para imprimir) ---- */
IF COL_LENGTH(N'dbo.Facturador', N'PuntoVenta') IS NULL
    ALTER TABLE dbo.Facturador ADD PuntoVenta INT NULL;
GO
IF COL_LENGTH(N'dbo.Facturador', N'Ambiente') IS NULL
    ALTER TABLE dbo.Facturador ADD Ambiente VARCHAR(4) NOT NULL
        CONSTRAINT DF_Facturador_Ambiente DEFAULT ('HOMO') WITH VALUES;
GO
IF COL_LENGTH(N'dbo.Facturador', N'CertificadoPath') IS NULL
    ALTER TABLE dbo.Facturador ADD CertificadoPath NVARCHAR(260) NULL, ClavePath NVARCHAR(260) NULL;
GO
IF COL_LENGTH(N'dbo.Facturador', N'Domicilio') IS NULL
    ALTER TABLE dbo.Facturador ADD Domicilio NVARCHAR(150) NULL, Localidad NVARCHAR(100) NULL,
        IngresosBrutos NVARCHAR(50) NULL, FechaInicioActividades DATE NULL;
GO

/* ---- Comprobante emitido para cada cobro ---- */
IF COL_LENGTH(N'dbo.Cobro', N'TipoComprobanteAFIP') IS NULL
    ALTER TABLE dbo.Cobro ADD TipoComprobanteAFIP INT NULL, PuntoVenta INT NULL, Numero INT NULL;
GO
IF COL_LENGTH(N'dbo.Cobro', N'CAE') IS NULL
    ALTER TABLE dbo.Cobro ADD CAE NVARCHAR(20) NULL, CAEVencimiento NVARCHAR(8) NULL, QRURLAFIP NVARCHAR(900) NULL;
GO
IF COL_LENGTH(N'dbo.Cobro', N'FechaEmision') IS NULL
    ALTER TABLE dbo.Cobro ADD FechaEmision DATETIME NULL, EmitidoPor UNIQUEIDENTIFIER NULL;
GO
/* Datos del receptor congelados al emitir (el comercio al que se le factura) */
IF COL_LENGTH(N'dbo.Cobro', N'ReceptorCuit') IS NULL
    ALTER TABLE dbo.Cobro ADD ReceptorCuit NVARCHAR(13) NULL, ReceptorNombre NVARCHAR(150) NULL,
        ReceptorDomicilio NVARCHAR(150) NULL, ReceptorCondicionIva NVARCHAR(60) NULL;
GO

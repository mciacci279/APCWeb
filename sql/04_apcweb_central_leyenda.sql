/* APC Web — leyenda al pie del ticket, configurable por empresa.
   Ejecutar UNA vez en APCWeb_Central. Las empresas existentes quedan con la leyenda de siempre. */
USE APCWeb_Central;
GO

IF COL_LENGTH(N'dbo.Empresa', N'LeyendaTicket') IS NULL
    ALTER TABLE dbo.Empresa ADD LeyendaTicket NVARCHAR(200) NULL
        CONSTRAINT DF_Empresa_LeyendaTicket DEFAULT (N'Muchas gracias por su compra!!!') WITH VALUES;
GO

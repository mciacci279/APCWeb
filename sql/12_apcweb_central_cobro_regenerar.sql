-- apcweb:titulo=Regenerar el cobro
-- apcweb:destino=central
/* APC Web — permitir volver a generar el cobro de un mes si el anterior se anuló.
   Ejecutar UNA vez en APCWeb_Central, después del script 10.
   Cambia la regla «una sola fila por empresa y mes» por «una sola fila NO anulada»,
   así queda el historial del cobro anulado y se puede crear el nuevo. */
USE APCWeb_Central;
GO

IF EXISTS (SELECT 1 FROM sys.objects WHERE name = 'UQ_Cobro_Empresa_Periodo' AND parent_object_id = OBJECT_ID(N'dbo.Cobro'))
    ALTER TABLE dbo.Cobro DROP CONSTRAINT UQ_Cobro_Empresa_Periodo;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Cobro_Empresa_Periodo_Activo' AND object_id = OBJECT_ID(N'dbo.Cobro'))
    CREATE UNIQUE INDEX UX_Cobro_Empresa_Periodo_Activo
        ON dbo.Cobro (EmpresaId, Periodo)
        WHERE Estado <> 'ANULADO';
GO

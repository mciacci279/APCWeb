-- apcweb:titulo=Módulos por empresa
-- apcweb:destino=central
/* ============================================================
   APC Web - 17 - Modulos habilitados por empresa
   Base: APCWeb_Central
   Idempotente.
   NULL o vacio = la empresa ve todos los modulos (como hasta ahora).
   ============================================================ */

IF COL_LENGTH('dbo.Empresa', 'Modulos') IS NULL
BEGIN
    ALTER TABLE dbo.Empresa ADD Modulos VARCHAR(200) NULL;
    PRINT 'Empresa.Modulos agregada. NULL = todos los modulos habilitados.';
END
ELSE
    PRINT 'Empresa.Modulos ya existia.';
GO

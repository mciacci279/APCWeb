-- apcweb:titulo=Módulos por rol
-- apcweb:destino=central
/* ============================================================
   APC Web - 19 - Modulos por rol dentro de cada empresa
   Base: APCWeb_Central
   Idempotente. Requiere el script 17.

   Empresa.Modulos          -> lo que tiene contratado la empresa (lo ve el administrador)
   Empresa.ModulosSupervisor-> lo que ve un supervisor
   Empresa.ModulosEmpleado  -> lo que ve un cajero
   NULL en las de rol = Facturar, Arqueo y Presupuestos, que es como venia funcionando.
   ============================================================ */

IF COL_LENGTH('dbo.Empresa', 'ModulosSupervisor') IS NULL
BEGIN
    ALTER TABLE dbo.Empresa ADD ModulosSupervisor VARCHAR(200) NULL;
    PRINT 'Empresa.ModulosSupervisor agregada.';
END
ELSE
    PRINT 'Empresa.ModulosSupervisor ya existia.';
GO

IF COL_LENGTH('dbo.Empresa', 'ModulosEmpleado') IS NULL
BEGIN
    ALTER TABLE dbo.Empresa ADD ModulosEmpleado VARCHAR(200) NULL;
    PRINT 'Empresa.ModulosEmpleado agregada.';
END
ELSE
    PRINT 'Empresa.ModulosEmpleado ya existia.';
GO

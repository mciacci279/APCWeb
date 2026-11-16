/* APC Web — rol Supervisor: ve las cajas de su sucursal, pero no administra la empresa.
   Ejecutar UNA vez en APCWeb_Central. Solo documenta el valor nuevo de Usuario.Rol
   ('Supervisor'), que convive con 'Admin' y 'Empleado'. Si la columna tiene una regla
   que limita los valores, acá se amplía. */
USE APCWeb_Central;
GO

DECLARE @ck SYSNAME = (SELECT TOP 1 c.name FROM sys.check_constraints c
                       WHERE c.parent_object_id = OBJECT_ID(N'dbo.Usuario')
                         AND c.definition LIKE '%Rol%');
IF @ck IS NOT NULL
BEGIN
    EXEC('ALTER TABLE dbo.Usuario DROP CONSTRAINT ' + @ck);
    ALTER TABLE dbo.Usuario ADD CONSTRAINT CK_Usuario_Rol
        CHECK (Rol IN ('SuperAdmin', 'Admin', 'Supervisor', 'Empleado'));
END
GO

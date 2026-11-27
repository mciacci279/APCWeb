-- apcweb:titulo=Descripción del producto de hasta 200 caracteres
-- apcweb:destino=empresa
/* ============================================================
   Factu360 - 30 - Producto.Nombre (descripción) a 200 caracteres
   Base: la de CADA EMPRESA (no la central)
   Idempotente: si ya tiene 200 o más, no hace nada.

   Mantiene el tipo (varchar/nvarchar) y si acepta nulos. Si hay
   índices que usan la columna, los quita, amplía la columna y los
   vuelve a crear igual que estaban.
   ============================================================ */
SET NOCOUNT ON;

DECLARE @tipo SYSNAME, @largo INT, @nulos BIT;
SELECT @tipo = t.name, @largo = c.max_length, @nulos = c.is_nullable
FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.Producto') AND c.name = 'Nombre';

IF @tipo IS NULL
BEGIN
    PRINT 'No se encontró dbo.Producto.Nombre.';
    RETURN;
END

-- max_length viene en bytes (nvarchar: 2 por carácter); -1 = MAX
DECLARE @caracteres INT = CASE WHEN @largo = -1 THEN 100000 WHEN @tipo IN ('nvarchar', 'nchar') THEN @largo / 2 ELSE @largo END;
IF @caracteres >= 200
BEGIN
    PRINT 'La descripción ya admite ' + CAST(@caracteres AS VARCHAR(10)) + ' caracteres: no hace falta cambiarla.';
    RETURN;
END

-- Índices (no clave primaria) que usan la columna: se guardan para recrearlos
DECLARE @indices TABLE (nombre SYSNAME, crear NVARCHAR(MAX));
INSERT INTO @indices (nombre, crear)
SELECT i.name,
       'CREATE ' + CASE WHEN i.is_unique = 1 THEN 'UNIQUE ' ELSE '' END + i.type_desc COLLATE DATABASE_DEFAULT
       + ' INDEX ' + QUOTENAME(i.name) + ' ON dbo.Producto ('
       + STUFF((SELECT ', ' + QUOTENAME(c.name) + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') + ')'
       + ISNULL(' INCLUDE (' + STUFF((SELECT ', ' + QUOTENAME(c.name)
                FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') + ')', '')
       + ISNULL(' WHERE ' + i.filter_definition, '')
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('dbo.Producto') AND i.is_primary_key = 0 AND i.type IN (1, 2)
  AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = 'Nombre');

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @n SYSNAME, @sql NVARCHAR(MAX);
    DECLARE q CURSOR LOCAL FOR SELECT nombre FROM @indices;
    OPEN q; FETCH NEXT FROM q INTO @n;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = 'DROP INDEX ' + QUOTENAME(@n) + ' ON dbo.Producto'; EXEC (@sql);
        FETCH NEXT FROM q INTO @n;
    END
    CLOSE q; DEALLOCATE q;

    SET @sql = 'ALTER TABLE dbo.Producto ALTER COLUMN Nombre ' + @tipo + '(200) ' + CASE WHEN @nulos = 1 THEN 'NULL' ELSE 'NOT NULL' END;
    EXEC (@sql);

    DECLARE r CURSOR LOCAL FOR SELECT crear FROM @indices;
    OPEN r; FETCH NEXT FROM r INTO @sql;
    WHILE @@FETCH_STATUS = 0 BEGIN EXEC (@sql); FETCH NEXT FROM r INTO @sql; END
    CLOSE r; DEALLOCATE r;

    COMMIT;
    PRINT 'Descripción ampliada a 200 caracteres (' + @tipo + ').';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT 'No se pudo ampliar la descripción: ' + ERROR_MESSAGE();
    THROW;
END CATCH
GO

/* ============================================================================
   APC Web - Limpiar la base de una empresa para entregarla al cliente

   QUE BORRA:  facturas, notas de credito, presupuestos, cajas, movimientos
               de stock y el historial de cambios de stock.
   QUE DEJA:   la empresa, sus datos fiscales, usuarios, sucursales, productos,
               rubros, clientes, formas de pago, tipos de precio y configuracion.

   COMO SE USA
   1) Abrir SSMS, SELECCIONAR LA BASE DE LA EMPRESA (no APCWeb_Central).
   2) Correrlo asi como esta: con @Simular = 1 NO borra nada, solo informa
      cuantas filas se irian en cada tabla.
   3) Si los numeros cierran, poner @Simular = 0 y volver a correrlo.

   HACER UN BACKUP ANTES. Lo que se borra no vuelve.
   ============================================================================ */

SET NOCOUNT ON;

DECLARE @Simular            BIT           = 1,        -- 1 = solo informar, 0 = borrar
        @SucursalABorrar    NVARCHAR(100) = NULL,     -- ej. N'ACME' (NULL = no borra ninguna)
        @BorrarClientes     BIT           = 0,        -- 1 = tambien borra los clientes cargados
        @PonerStockEnCero   BIT           = 0;        -- 1 = deja todo el stock en cero

PRINT '=== Base: ' + DB_NAME() + ' ===';
IF @Simular = 1 PRINT '*** MODO SIMULACION: no se borra nada ***';
PRINT '';

/* ---------------------------------------------------------------- informe */
DECLARE @t TABLE (Orden INT IDENTITY, Tabla SYSNAME, Filas INT);
DECLARE @tabla SYSNAME, @sql NVARCHAR(400), @filas INT;

DECLARE tablas CURSOR LOCAL FAST_FORWARD FOR
    SELECT v.t FROM (VALUES
        ('Detalle'), ('Factura'),
        ('DetalleNotaCredito'), ('NotaCredito'),
        ('DetallePresupuesto'), ('Presupuesto'),
        ('CajaTurno'), ('CajaIngreso'), ('EgresoCaja'),
        ('MovimientoStock'), ('ProductoCambioStock')
    ) v(t)
    WHERE OBJECT_ID(N'dbo.' + v.t, N'U') IS NOT NULL;

OPEN tablas;
FETCH NEXT FROM tablas INTO @tabla;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@tabla);
    EXEC sp_executesql @sql, N'@n INT OUTPUT', @n = @filas OUTPUT;
    INSERT INTO @t (Tabla, Filas) VALUES (@tabla, @filas);
    FETCH NEXT FROM tablas INTO @tabla;
END
CLOSE tablas; DEALLOCATE tablas;

SELECT Tabla, Filas AS [Filas a borrar] FROM @t ORDER BY Orden;

IF @Simular = 1
BEGIN
    PRINT 'Nada se borro. Para borrar de verdad, poner @Simular = 0 arriba.';
    RETURN;
END

/* ---------------------------------------------------------------- borrado */
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Detalle', N'U')              IS NOT NULL DELETE FROM dbo.Detalle;
    IF OBJECT_ID(N'dbo.DetalleNotaCredito', N'U')   IS NOT NULL DELETE FROM dbo.DetalleNotaCredito;
    IF OBJECT_ID(N'dbo.NotaCredito', N'U')          IS NOT NULL DELETE FROM dbo.NotaCredito;
    IF OBJECT_ID(N'dbo.Factura', N'U')              IS NOT NULL DELETE FROM dbo.Factura;
    IF OBJECT_ID(N'dbo.DetallePresupuesto', N'U')   IS NOT NULL DELETE FROM dbo.DetallePresupuesto;
    IF OBJECT_ID(N'dbo.Presupuesto', N'U')          IS NOT NULL DELETE FROM dbo.Presupuesto;
    IF OBJECT_ID(N'dbo.CajaIngreso', N'U')          IS NOT NULL DELETE FROM dbo.CajaIngreso;
    IF OBJECT_ID(N'dbo.EgresoCaja', N'U')           IS NOT NULL DELETE FROM dbo.EgresoCaja;
    IF OBJECT_ID(N'dbo.CajaTurno', N'U')            IS NOT NULL DELETE FROM dbo.CajaTurno;
    IF OBJECT_ID(N'dbo.MovimientoStock', N'U')      IS NOT NULL DELETE FROM dbo.MovimientoStock;
    IF OBJECT_ID(N'dbo.ProductoCambioStock', N'U')  IS NOT NULL DELETE FROM dbo.ProductoCambioStock;

    IF @BorrarClientes = 1 AND OBJECT_ID(N'dbo.Cliente', N'U') IS NOT NULL
        DELETE FROM dbo.Cliente;

    IF @PonerStockEnCero = 1
    BEGIN
        IF OBJECT_ID(N'dbo.StockSucursal', N'U') IS NOT NULL UPDATE dbo.StockSucursal SET Stock = 0;
        IF COL_LENGTH('dbo.Producto', 'Stock') IS NOT NULL   UPDATE dbo.Producto SET Stock = 0;
    END

    /* Sucursal de prueba: se borra con su stock, siempre que quede otra activa */
    IF @SucursalABorrar IS NOT NULL AND OBJECT_ID(N'dbo.Sucursal', N'U') IS NOT NULL
    BEGIN
        DECLARE @sucId UNIQUEIDENTIFIER =
            (SELECT TOP 1 Id FROM dbo.Sucursal WHERE Nombre LIKE N'%' + @SucursalABorrar + N'%');

        IF @sucId IS NULL
            PRINT 'No se encontro ninguna sucursal que contenga: ' + @SucursalABorrar;
        ELSE IF (SELECT COUNT(*) FROM dbo.Sucursal) <= 1
            PRINT 'No se borro la sucursal: tiene que quedar al menos una.';
        ELSE
        BEGIN
            IF COL_LENGTH('dbo.Usuario', 'SucursalId') IS NOT NULL
                UPDATE dbo.Usuario SET SucursalId = NULL WHERE SucursalId = @sucId;
            IF OBJECT_ID(N'dbo.StockSucursal', N'U') IS NOT NULL
                DELETE FROM dbo.StockSucursal WHERE SucursalId = @sucId;
            DELETE FROM dbo.Sucursal WHERE Id = @sucId;
            PRINT 'Sucursal borrada: ' + @SucursalABorrar;
        END
    END

    COMMIT;
    PRINT 'Listo: la base quedo limpia y lista para entregar.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT 'NO se borro nada. Error: ' + ERROR_MESSAGE();
    PRINT 'Si menciona una FOREIGN KEY, pasame el mensaje y agrego esa tabla al script.';
    RETURN;
END CATCH
GO

/* ----------------------------------------- numeracion interna desde cero */
/* Solo afecta a los contadores internos (ID #, numero de presupuesto).
   El numero de factura lo sigue dando ARCA segun el punto de venta.        */
IF OBJECT_ID(N'dbo.Factura', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = OBJECT_ID(N'dbo.Factura'))
    DBCC CHECKIDENT ('dbo.Factura', RESEED, 0) WITH NO_INFOMSGS;

IF OBJECT_ID(N'dbo.Presupuesto', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = OBJECT_ID(N'dbo.Presupuesto'))
    DBCC CHECKIDENT ('dbo.Presupuesto', RESEED, 0) WITH NO_INFOMSGS;

IF OBJECT_ID(N'dbo.NotaCredito', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = OBJECT_ID(N'dbo.NotaCredito'))
    DBCC CHECKIDENT ('dbo.NotaCredito', RESEED, 0) WITH NO_INFOMSGS;
GO

/* --------------------------------------------------- control de como quedo */
SELECT 'Empresa'   AS Tabla, COUNT(*) AS Filas FROM dbo.Empresa
UNION ALL SELECT 'Sucursal',   COUNT(*) FROM dbo.Sucursal
UNION ALL SELECT 'Usuario',    COUNT(*) FROM dbo.Usuario
UNION ALL SELECT 'Producto',   COUNT(*) FROM dbo.Producto
UNION ALL SELECT 'Cliente',    COUNT(*) FROM dbo.Cliente
UNION ALL SELECT 'FormaPago',  COUNT(*) FROM dbo.FormaPago
UNION ALL SELECT 'Factura',    COUNT(*) FROM dbo.Factura
UNION ALL SELECT 'Presupuesto',COUNT(*) FROM dbo.Presupuesto
UNION ALL SELECT 'CajaTurno',  COUNT(*) FROM dbo.CajaTurno;

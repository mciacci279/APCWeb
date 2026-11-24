/* ============================================================================
   Factu360 - Borrar los comprobantes de UN DIA de una empresa de prueba

   QUE BORRA (solo los del dia elegido):
     facturas con y sin CAE y notas de pedido (Factura + Detalle),
     notas de credito (NotaCredito + DetalleNotaCredito),
     cobros con QR de Mercado Pago (PagoQr),
     y si @BorrarCaja = 1: turnos de caja abiertos ese dia, ingresos y retiros.
   QUE DEJA: todo lo demas (productos, clientes, usuarios, cajas, sesiones de caja).

   COMO SE USA
   1) Abrir SSMS y SELECCIONAR LA BASE DE LA EMPRESA "Test" (no APCWeb_Central).
   2) Correrlo asi como esta: con @Simular = 1 NO borra nada, solo informa.
   3) Si los numeros cierran, poner @Simular = 0 y volver a correrlo.

   Por seguridad, si la empresa de la base no se llama como @Empresa, no hace nada.
   OJO: las facturas con CAE quedan informadas en ARCA aunque se borren de aca.
   La proxima factura toma el numero que diga ARCA, asi que la numeracion sigue bien.
   ============================================================================ */

SET NOCOUNT ON;

DECLARE @Simular       BIT           = 1,              -- 1 = solo informar, 0 = borrar
        @Empresa       NVARCHAR(100) = N'Test',        -- nombre (o parte) de la empresa
        @Dia           DATE          = CAST(GETDATE() AS DATE),   -- hoy; o p.ej. '2026-11-24'
        @BorrarCaja    BIT           = 1,              -- 1 = tambien turnos, ingresos y retiros de caja del dia
        @DevolverStock BIT           = 1;              -- 1 = devuelve al stock lo vendido en las facturas borradas

DECLARE @desde DATETIME = @Dia, @hasta DATETIME = DATEADD(DAY, 1, @Dia);

/* ---------------------------------------------------------- control de empresa */
DECLARE @nombre NVARCHAR(200) = (SELECT TOP 1 Nombre FROM dbo.Empresa);
PRINT '=== Base: ' + DB_NAME() + ' · Empresa: ' + ISNULL(@nombre, '(sin nombre)') + ' · Dia: ' + CONVERT(VARCHAR(10), @Dia, 103) + ' ===';

IF ISNULL(@nombre, N'') NOT LIKE N'%' + @Empresa + N'%' AND DB_NAME() NOT LIKE N'%' + @Empresa + N'%'
BEGIN
    PRINT 'ESTA BASE NO ES DE LA EMPRESA "' + @Empresa + '". No se borro nada.';
    PRINT 'Elegi la base correcta arriba en SSMS (desplegable de bases) y volve a correrlo.';
    RETURN;
END

/* ---------------------------------------------------------- que se va a borrar */
DECLARE @fac TABLE (Id UNIQUEIDENTIFIER PRIMARY KEY, Anulada BIT);
INSERT INTO @fac SELECT Id, ISNULL(Anulada, 0) FROM dbo.Factura WHERE Fecha >= @desde AND Fecha < @hasta;

DECLARE @nc TABLE (Id UNIQUEIDENTIFIER PRIMARY KEY);
IF OBJECT_ID(N'dbo.NotaCredito', N'U') IS NOT NULL
    INSERT INTO @nc SELECT Id FROM dbo.NotaCredito WHERE Fecha >= @desde AND Fecha < @hasta;

SELECT 'Facturas / notas de pedido' AS Que, COUNT(*) AS Cantidad,
       ISNULL((SELECT SUM(f.MontoTotal) FROM dbo.Factura f JOIN @fac x ON x.Id = f.Id), 0) AS Importe
FROM @fac
UNION ALL SELECT '   con CAE', COUNT(*), ISNULL(SUM(f.MontoTotal), 0)
          FROM dbo.Factura f JOIN @fac x ON x.Id = f.Id WHERE f.CAE IS NOT NULL AND f.CAE <> ''
UNION ALL SELECT '   sin CAE / notas de pedido', COUNT(*), ISNULL(SUM(f.MontoTotal), 0)
          FROM dbo.Factura f JOIN @fac x ON x.Id = f.Id WHERE f.CAE IS NULL OR f.CAE = ''
UNION ALL SELECT 'Renglones de facturas', COUNT(*), NULL
          FROM dbo.Detalle d JOIN @fac x ON x.Id = d.FacturaId
UNION ALL SELECT 'Notas de credito', COUNT(*), NULL FROM @nc;

IF @BorrarCaja = 1
    SELECT 'Turnos de caja' AS Que, COUNT(*) AS Cantidad FROM dbo.CajaTurno WHERE FechaApertura >= @desde AND FechaApertura < @hasta
    UNION ALL SELECT 'Ingresos de caja', COUNT(*) FROM dbo.CajaIngreso WHERE Fecha >= @desde AND Fecha < @hasta
    UNION ALL SELECT 'Retiros de caja',  COUNT(*) FROM dbo.EgresoCaja  WHERE Fecha >= @desde AND Fecha < @hasta;

IF @Simular = 1
BEGIN
    PRINT 'MODO SIMULACION: no se borro nada. Para borrar de verdad, poner @Simular = 0 arriba.';
    RETURN;
END

/* ---------------------------------------------------------- borrado */
BEGIN TRY
    BEGIN TRANSACTION;

    /* Stock: se devuelve lo de las facturas no anuladas (las anuladas ya lo devolvieron con su nota de credito) */
    IF @DevolverStock = 1
    BEGIN
        DECLARE @cant TABLE (ProductoId UNIQUEIDENTIFIER, Terminal INT, Cant DECIMAL(18, 4));
        INSERT INTO @cant
        SELECT d.ProductoId, f.Terminal, SUM(d.CantidadRenglon)
        FROM dbo.Detalle d
        JOIN dbo.Factura f ON f.Id = d.FacturaId
        JOIN @fac x ON x.Id = f.Id AND x.Anulada = 0
        WHERE d.ProductoId IS NOT NULL
        GROUP BY d.ProductoId, f.Terminal;

        UPDATE p SET p.Stock = ISNULL(p.Stock, 0) + c.Cant
        FROM dbo.Producto p
        JOIN (SELECT ProductoId, SUM(Cant) AS Cant FROM @cant GROUP BY ProductoId) c ON c.ProductoId = p.Id
        WHERE ISNULL(p.OmitirControlStock, 0) = 0;

        IF OBJECT_ID(N'dbo.StockSucursal', N'U') IS NOT NULL
            UPDATE ss SET ss.Stock = ss.Stock + c.Cant
            FROM dbo.StockSucursal ss
            JOIN dbo.Sucursal s ON s.Id = ss.SucursalId
            JOIN @cant c ON c.ProductoId = ss.ProductoId AND c.Terminal = s.PuntoVenta
            JOIN dbo.Producto p ON p.Id = ss.ProductoId AND ISNULL(p.OmitirControlStock, 0) = 0;
    END

    IF OBJECT_ID(N'dbo.PagoQr', N'U') IS NOT NULL
        DELETE FROM dbo.PagoQr
        WHERE (Fecha >= @desde AND Fecha < @hasta) OR FacturaId IN (SELECT Id FROM @fac);

    IF OBJECT_ID(N'dbo.DetalleNotaCredito', N'U') IS NOT NULL
        DELETE FROM dbo.DetalleNotaCredito WHERE NotaCreditoId IN (SELECT Id FROM @nc);
    IF OBJECT_ID(N'dbo.NotaCredito', N'U') IS NOT NULL
        DELETE FROM dbo.NotaCredito WHERE Id IN (SELECT Id FROM @nc);

    DELETE FROM dbo.Detalle WHERE FacturaId IN (SELECT Id FROM @fac);
    DELETE FROM dbo.Factura WHERE Id IN (SELECT Id FROM @fac);

    IF @BorrarCaja = 1
    BEGIN
        DELETE FROM dbo.CajaIngreso WHERE Fecha >= @desde AND Fecha < @hasta;
        DELETE FROM dbo.EgresoCaja  WHERE Fecha >= @desde AND Fecha < @hasta;
        DELETE FROM dbo.CajaTurno   WHERE FechaApertura >= @desde AND FechaApertura < @hasta;
    END

    COMMIT;
    PRINT 'Listo: se borraron los comprobantes del ' + CONVERT(VARCHAR(10), @Dia, 103) + '.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT 'NO se borro nada. Error: ' + ERROR_MESSAGE();
    PRINT 'Si menciona una FOREIGN KEY, pasame el mensaje y agrego esa tabla al script.';
END CATCH
GO

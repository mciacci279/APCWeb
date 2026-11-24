-- apcweb:titulo=Apertura y cierre de caja por cada caja de la sucursal
-- apcweb:destino=empresa
/* ============================================================
   Factu360 - 28 - Apertura de caja por caja (no por usuario)
   Base: la de CADA EMPRESA (no la central)
   Idempotente: se puede correr las veces que haga falta.
   Requiere el script 27 (PuestoCaja).

   CajaTurno.PuestoCajaId: si la sucursal tiene varias cajas, cada
     caja se abre y se cierra por separado, con su propio monto
     inicial y horario. El turno es de la caja: si cambia el cajero,
     el turno sigue abierto. Null = turno por usuario (como antes).
   CajaTurno.UsuarioCierreId: quien cerró la caja.
   UX_CajaTurno_Puesto_Abierto: una caja no puede tener dos
     turnos abiertos al mismo tiempo.
   ============================================================ */
IF COL_LENGTH('dbo.CajaTurno', 'PuestoCajaId') IS NULL
BEGIN
    ALTER TABLE dbo.CajaTurno ADD PuestoCajaId UNIQUEIDENTIFIER NULL;
    PRINT 'Columna CajaTurno.PuestoCajaId agregada.';
END
GO

IF COL_LENGTH('dbo.CajaTurno', 'UsuarioCierreId') IS NULL
BEGIN
    ALTER TABLE dbo.CajaTurno ADD UsuarioCierreId UNIQUEIDENTIFIER NULL;
    PRINT 'Columna CajaTurno.UsuarioCierreId agregada.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CajaTurno_Puesto_Abierto' AND object_id = OBJECT_ID('dbo.CajaTurno'))
BEGIN
    CREATE UNIQUE INDEX UX_CajaTurno_Puesto_Abierto ON dbo.CajaTurno (PuestoCajaId)
        WHERE FechaCierre IS NULL AND PuestoCajaId IS NOT NULL;
    PRINT 'Indice UX_CajaTurno_Puesto_Abierto creado.';
END
GO

-- apcweb:titulo=Lectura de codigos de balanza Systel
-- apcweb:destino=empresa
/* ============================================================
   APC Web - 25 - Codigos de barra de balanza Systel
   Base: la de CADA EMPRESA (no la central)
   Idempotente: se puede correr las veces que haga falta.

   El codigo que imprime la balanza tiene 13 digitos:
     20 PPPP CCCCCC X   venta por peso   (PPPP = PLU, CCCCCC = gramos)
     21 PPPP CCCCCC X   venta por unidad (CCCCCC = unidades)
     22 AA IIIIIIII X   total del ticket (AA = articulos, I = importe)

   El PLU se busca en Producto.Codigo, sin los ceros de la izquierda.
   ============================================================ */

IF OBJECT_ID(N'dbo.ApcWebBalanza', N'U') IS NULL
    CREATE TABLE dbo.ApcWebBalanza (
        Unica      BIT           NOT NULL CONSTRAINT PK_ApcWebBalanza PRIMARY KEY
                                 CONSTRAINT DF_ApcWebBalanza_Unica DEFAULT (1),
        Activa     BIT           NOT NULL CONSTRAINT DF_ApcWebBalanza_Activa DEFAULT (0),
        /* Q = el codigo trae la cantidad y el precio sale del producto
           P = el codigo trae el importe ya calculado por la balanza     */
        Modo       VARCHAR(1)    NOT NULL CONSTRAINT DF_ApcWebBalanza_Modo DEFAULT ('Q'),
        /* 0 = el importe viene entero (5200 = $5200), 1 = con dos decimales (5200 = $52,00) */
        Decimales  BIT           NOT NULL CONSTRAINT DF_ApcWebBalanza_Decimales DEFAULT (0),
        /* Producto comodin al que se le carga el total cuando se lee un codigo 22 */
        CodigoSuma NVARCHAR(30)  NULL);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.ApcWebBalanza)
    INSERT INTO dbo.ApcWebBalanza (Unica, Activa, Modo, Decimales, CodigoSuma)
    VALUES (1, 0, 'Q', 0, N'1234567890');
GO

PRINT 'Script 25 listo: lectura de codigos de balanza.';

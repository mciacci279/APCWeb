/* APC Web — carga en el tablero (columna Ideas) todo lo que quedó pendiente al 16/11/2026.
   Ejecutar en APCWeb_Central. Se puede correr más de una vez: no duplica,
   saltea las tarjetas que ya existan con el mismo título. */
USE APCWeb_Central;
GO

DECLARE @pendientes TABLE (Titulo NVARCHAR(150), Detalle NVARCHAR(MAX), Tipo VARCHAR(16), Prioridad VARCHAR(10), Orden INT);

INSERT INTO @pendientes (Titulo, Detalle, Tipo, Prioridad, Orden) VALUES

-- ---------- Seguridad del servidor (conviene moverlas a Pendiente) ----------
(N'HTTPS con Let''s Encrypt en el VPS',
 N'Instalar win-acme y emitir el certificado para apc.cm-tech.com.ar. Abrir el puerto 443 en el firewall del proveedor y en Windows. Después: cargar UrlPublica con https en appsettings.Secretos.json y pedir que la cookie de sesión viaje solo por HTTPS. Urgente: hoy las claves viajan sin cifrar y los links de factura que se mandan por WhatsApp muestran "sitio no seguro".',
 'TAREA', 'ALTA', 1),

(N'Cambiar la clave de sa y crear el usuario apcweb',
 N'La clave de sa quedó expuesta. Crear un usuario SQL propio para la web con permisos de lectura, escritura y ejecución sobre APCWeb_Central y las bases de las empresas (y dbcreator si se usa "Crear base desde la plantilla"), y reemplazar sa en todas las cadenas de conexión.',
 'TAREA', 'ALTA', 2),

(N'Cerrar el puerto 1433 hacia internet',
 N'Si la web y SQL están en el mismo VPS, la conexión va por .\SQLEXPRESS y no hace falta exponerlo. Para administrar desde afuera, limitar por IP o usar escritorio remoto.',
 'TAREA', 'ALTA', 3),

(N'Backups automáticos de las bases y de C:\APCWeb',
 N'SQL Express no trae programador: armar una tarea de Windows que respalde APCWeb_Central y cada base de empresa todos los días, con copia fuera del VPS. Incluir C:\APCWeb\Certificados y C:\APCWeb\Datos\enlaces.key: si se pierde esa clave, dejan de funcionar los links de comprobantes ya enviados.',
 'TAREA', 'ALTA', 4),

(N'Permisos de carpetas y puertos del VPS',
 N'Datos, Certificados y Logs con permiso de Modificar solo para IIS AppPool\APCWeb y administradores. Limitar por IP los puertos 8172 (Web Deploy) y 3389 (escritorio remoto). Dejar el Servicio de administración web (WMSvc) en inicio Automático: se cayó el 16/11 y no dejaba publicar.',
 'TAREA', 'NORMAL', 5),

(N'SQL Server 2014 sin soporte',
 N'Ya no recibe parches. Primero instalar el Service Pack 3; después planificar la migración a SQL Server 2022 Express.',
 'TAREA', 'NORMAL', 6),

(N'Sacar los .key de C:\APC Code y guardar aparte las credenciales',
 N'Los certificados y el archivo "Credenciales APC web.txt" están en texto plano en la carpeta del código. No se suben a GitHub, pero conviene moverlos a un lugar seguro.',
 'TAREA', 'NORMAL', 7),

(N'Repositorio privado y organización de GitHub para CM Tech',
 N'Dejar APCWeb en privado (Settings → Change visibility) y crear una organización gratuita a nombre de CM Tech para que los proyectos no dependan de una cuenta personal.',
 'TAREA', 'BAJA', 8),

(N'Activar el doble factor en cada usuario de CM Tech',
 N'Desde Configuración → Verificación en dos pasos, con Google o Microsoft Authenticator. Es por usuario: cada uno lo activa en su sesión.',
 'TAREA', 'NORMAL', 9),

-- ---------- Pruebas que faltan ----------
(N'Probar facturas con descuento y con recargo',
 N'Emitir una con descuento por producto y otra con recargo sobre el total, y confirmar que ARCA da el CAE y que los importes cierran con el Libro IVA.',
 'TAREA', 'ALTA', 10),

(N'Probar la nota de crédito con sucursales',
 N'Que salga con el mismo punto de venta de la factura que anula y que el stock vuelva a la sucursal correcta.',
 'TAREA', 'ALTA', 11),

(N'Probar una segunda sucursal de punta a punta',
 N'Crearla desde Empresas → Sucursales, asignarle un cajero, facturar con su punto de venta, transferir stock y confirmar que el total del producto no se desarma.',
 'TAREA', 'NORMAL', 12),

(N'Probar los tres roles',
 N'Entrar como cajero, supervisor y administrador, y confirmar que cada uno ve solo las cajas que le corresponden.',
 'TAREA', 'NORMAL', 13),

(N'Verificar el Libro IVA Ventas contra ARCA',
 N'Comparar el período completo con lo que figura en el portal de ARCA.',
 'TAREA', 'NORMAL', 14),

-- ---------- Funcionalidad pendiente ----------
(N'Importador de Excel: cargar el stock por sucursal',
 N'Hoy el importador sigue escribiendo el stock total y no lo reparte por sucursal. Quedó así a propósito para no tocarlo sin poder probarlo.',
 'MEJORA', 'NORMAL', 20),

(N'Guardar el PDF del comprobante en el servidor',
 N'Que al emitir quede el archivo en una carpeta por empresa. Es la base para mandar la factura adjunta por email o WhatsApp, en lugar de solo el link.',
 'MEJORA', 'BAJA', 21),

(N'Enviar comprobantes por email',
 N'Falta configurar el SMTP y UrlPublica en el servidor. Decidir si se manda un link o el PDF adjunto.',
 'MEJORA', 'BAJA', 22),

(N'Nota de crédito parcial',
 N'Hoy solo se puede anular por el total. Permitir elegir renglones o un importe menor.',
 'MEJORA', 'NORMAL', 23),

(N'Pago combinado y vuelto',
 N'Permitir cobrar una parte en efectivo y otra con tarjeta, y volver a mostrar el campo "Paga con" que está oculto.',
 'MEJORA', 'BAJA', 24),

(N'Stock: ingreso de mercadería y aviso de stock mínimo',
 N'Carga de compras a proveedor y aviso cuando un producto queda por debajo del mínimo en una sucursal.',
 'MEJORA', 'NORMAL', 25),

(N'Clientes con cuenta corriente',
 N'Quedó fuera cuando se hizo el ABM de clientes: saldos, pagos a cuenta y resumen.',
 'MEJORA', 'BAJA', 26),

(N'Habilitar el padrón de ARCA para cada empresa',
 N'El botón "Traer de ARCA" en Clientes necesita que el certificado de cada empresa esté habilitado para el servicio "Consulta de padrón alcance 13". CGTEC todavía no lo tiene.',
 'TAREA', 'NORMAL', 27),

(N'Crear la base plantilla SuperMarketDB_Plantilla',
 N'Una base limpia con los scripts 01 y 07 aplicados, para que funcione "Crear base desde la plantilla" al dar de alta una empresa nueva.',
 'TAREA', 'NORMAL', 28),

(N'Impresión directa del ticket',
 N'Hoy abre el diálogo del navegador. Ver si conviene una impresora térmica configurada como predeterminada o alguna extensión.',
 'MEJORA', 'BAJA', 29),

(N'Sitio de pruebas antes de publicar',
 N'Una copia en homologación para probar los cambios sin tocar lo que usan los clientes.',
 'MEJORA', 'BAJA', 30),

(N'Pruebas automáticas de los cálculos',
 N'Tests de IVA, descuentos, recargos y arqueo, para que GitHub avise si algo se rompe.',
 'MEJORA', 'BAJA', 31);

INSERT INTO dbo.Ticket (Id, Titulo, Detalle, Estado, Prioridad, Tipo, Orden)
SELECT NEWID(), p.Titulo, p.Detalle, 'IDEA', p.Prioridad, p.Tipo, p.Orden
FROM @pendientes p
WHERE NOT EXISTS (SELECT 1 FROM dbo.Ticket t WHERE t.Titulo = p.Titulo);

SELECT CAST(@@ROWCOUNT AS VARCHAR(10)) + ' tarjeta(s) agregadas en Ideas.' AS Resultado;
GO

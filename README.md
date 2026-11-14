# APC Web — Facturación y caja

Versión web simple de APC: **facturar**, **arqueo de caja**, **productos** (ABM, precios y stock con historial) y **usuarios**.
Trabaja sobre la **misma base de datos** que APC.WinForms, así que los dos sistemas
pueden convivir con el mismo cliente.

Diseño: variante **A2 · Menú compacto + vuelto**.

## Arquitectura

Un solo sitio web para todas las empresas. Detrás:

- **Base central `APCWeb_Central`** (de CM Tech): empresas, usuarios web con clave hasheada (PBKDF2-SHA256) y registro de ingresos.
- **Una base por empresa** (`SuperMarketDB_<cliente>`): facturas, productos, caja. La web elige la base según la empresa del usuario.
- Cada usuario web tiene además una fila "espejo" sin clave en `dbo.Usuario` de la base de su empresa, con el mismo Id, para que las facturas y la caja apunten a él.

| Rol | Ingresa con | Ve |
|---|---|---|
| CM Tech (SuperAdmin) | empresa `cmtech` | Panel de empresas: alta, datos de ARCA, usuarios, suspensión |
| Administrador (dueño) | el código de su empresa | Facturar, Arqueo, Productos, Comprobantes, Usuarios |
| Empleado | el código de su empresa | Facturar y Arqueo |

Seguridad del ingreso: 5 claves incorrectas bloquean el usuario 15 minutos; el dueño (o CM Tech) lo desbloquea cambiándole la clave. Empresa suspendida = nadie de esa empresa entra.

## Estructura

| Proyecto | Qué tiene |
|---|---|
| `src/APC.Arca` | `WsaaAuth` y `Wsfev1Client` copiados de APC.WinForms + `ArcaService` |
| `src/APC.Core` | Lógica y acceso a datos (Dapper): facturación, caja, productos, usuarios, empresas |
| `src/APC.Web` | Blazor Server (.NET 9) |
| `sql/01_apcweb_cajaturno.sql` | Tabla `CajaTurno`, en la base de **cada empresa** |
| `sql/02_apcweb_central.sql` | Crea la base central `APCWeb_Central` (una sola vez) |

## Scripts de base de datos

Se ejecutan una sola vez, en orden. Los `*_central_*` van en `APCWeb_Central`; los demás, en la base de **cada empresa**.

| Script | Dónde | Qué agrega |
|---|---|---|
| `01_apcweb_cajaturno.sql` | empresa | Apertura y cierre de caja |
| `02_apcweb_central.sql` | central | Empresa, Usuario, IngresoLog |
| `03_apcweb_central_logo.sql` | central | Logo de la empresa |
| `04_apcweb_central_leyenda.sql` | central | Leyenda del ticket |
| `05_apcweb_central_recupero.sql` | central | Recupero de clave por email |
| `06_apcweb_central_fantasia.sql` | central | Nombre de fantasía |
| `07_apcweb_sucursales.sql` | empresa | Sucursales, stock por sucursal y movimientos |
| `08_apcweb_central_sucursal_usuario.sql` | central | Sucursal asignada a cada usuario |
| `09_apcweb_central_abono_2fa.sql` | central | Control de abono y verificación en dos pasos |
| `10_apcweb_central_cobranzas.sql` | central | Cobranzas de CM Tech: facturadores, cobros y avisos |
| `11_apcweb_central_factura_abono.sql` | central | Factura C del abono: certificado por facturador y CAE |

## Primera vez después de clonar desde GitHub

Por seguridad, estos archivos **no están en el repositorio** (ver `.gitignore`):

| Falta | Qué hacer |
|---|---|
| `src/APC.Web/appsettings.Secretos.json` | Copiar `appsettings.Secretos.example.json` con ese nombre y completar la conexión a `APCWeb_Central` (y SMTP si se prueba el recupero de clave). **Sin este archivo la app no arranca.** Pedir los datos a CM Tech por un medio privado, nunca por GitHub. |
| `bin/`, `obj/`, `.vs/` | Nada: Visual Studio los genera al compilar (restaura los paquetes NuGet solo). |
| Perfil de publicación (`Properties/PublishProfiles`, `*.PublishSettings`) | Solo si va a publicar al VPS: importar el `.PublishSettings` que le pase CM Tech, o crear el perfil Web Deploy. |
| Certificados ARCA (`.crt` / `.key`) | Nada en la PC: viven en el servidor y se suben desde el panel Empresas. |

Requisitos: Visual Studio 2022 con el SDK de .NET 9.

## Puesta en marcha

1. Ejecutar `sql/02_apcweb_central.sql` en el SQL Server (una sola vez).
2. En `src/APC.Web`, crear `appsettings.Secretos.json` (copiando `appsettings.Secretos.example.json`) con la conexión a `APCWeb_Central`. No se versiona ni se publica.
3. Compilar y ejecutar. Abrir **`/instalar`** y crear el usuario de CM Tech. La página se deshabilita sola después.
4. Ingresar con empresa **`cmtech`** → **Empresas → Nueva empresa**: código, conexión a su base, punto de venta, ambiente y certificados → **Probar conexión** → Crear.
5. En la pestaña **Usuarios** de esa empresa, crear el administrador (el dueño).
6. En la base de esa empresa, ejecutar `sql/01_apcweb_cajaturno.sql`.
7. Pasarle al cliente su link: `https://apc.cm-tech.com.ar/ingresar?e=<código>`.

`appsettings.Tenants.json` ya no se usa: las empresas viven en la base central.

## Cómo replica a APC.WinForms

- **Precios con IVA incluido.** El IVA del renglón es `monto − monto / (1 + alícuota)`. La alícuota es la general (`Iva.Porcentaje` de la condición del cliente), `PROD_IVA_DIF` si el producto es `IvaDiferenciado`, o 0 / no gravado si es `IvaExento`.
- **Forma de pago.** Se aplica primero el descuento y después el recargo, igual que `btnAgregar_Click`.
- **Letra del comprobante.** C si la empresa es monotributo, A si el cliente es responsable inscripto, y B en el resto de los casos.
- **Flujo de emisión.** Se guarda `Factura` + `Detalle` (y se descuenta stock si `FACT_DECREMENTA`), se pide el CAE y se actualizan `Numero`, `CAE`, `QRParametros`, `QRURLAFIP`, `QRCode` y `MontoIvaTipoAfip4/5/6`.
- **Si ARCA no responde**, la factura queda guardada sin CAE y se puede reintentar desde Facturar o desde Comprobantes.
- **Usuarios.** Los clientes web no usan APC de escritorio, así que los usuarios web viven en la base central con hash seguro; no comparten clave con el escritorio.
- **Productos.** Cada cambio de precio o stock, incluido el alta y el aumento masivo por rubro o de todos en % o $, deja una fila en `ProductoCambioStock` con el mismo formato que APC. Los precios se calculan desde el costo con `PORC_PVTA1..3`. Solo los perfiles administradores pueden modificar; el resto solo consulta.
- **Precio en la venta.** Si el producto tiene precio 0 (ej. «varios»), el vendedor lo escribe al facturar. En cualquier otro renglón se puede tocar el precio para cambiarlo solo en esa factura.
- **Arqueo.** Lee la vista existente `VArqueoCaja`, la misma del reporte de APC. El turno (apertura con monto inicial y cierre con conteo) es lo único nuevo.

## Para probar primero (no se pudo compilar acá)

El código se escribió sin acceso a un SDK de .NET, así que **la primera compilación puede tirar algún error menor**. Pasame la lista de errores y los corrijo. Después, en homologación:

1. Ingresar con un usuario existente. Esto confirma que el cifrado de claves coincide.
2. Abrir caja → facturar B a consumidor final → verificar CAE y QR en el ticket.
3. Repetir con un producto de IVA 10,5 % y otro exento.
4. Factura A a un CUIT de prueba.
5. Revisar en APC de escritorio que la factura aparece y que el arqueo coincide.

## Pendientes conocidos

- **Parte 2 (pendiente):** ver el registro de ingresos desde el panel y recuperar la clave por email.
- **Certificados en el repositorio.** `C:\APC Code\APC` tiene `clave.key` y `clave_PROD.key` junto al código: hay que sacarlos del control de código.
- **Fuera de este alcance:** cuenta corriente, notas de crédito, listas de precio 2 y 3, y la balanza.
- **QR.** Se guarda como PNG (APC guardaba JPEG). Si algún reporte de Crystal lee `QRCode`, verificarlo.
- **Efectivo esperado del arqueo.** Suma los movimientos cuya forma de pago contiene "efectivo" más los ingresos y egresos de caja. Si un cliente usa otro nombre para efectivo, ajustarlo en `CajaService`.

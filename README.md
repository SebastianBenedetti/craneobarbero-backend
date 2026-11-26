# Cráneo Barbero — Backend

API de la barbería Cráneo Barbero: cuentas, turnos, agenda, pagos, tienda y stock, precios, reportes, reseñas y asistente de reservas.

Hecha con ASP.NET Core (.NET 10) sobre PostgreSQL. Se despliega en Render con Docker.

## Estructura

- `src/CraneoBarbero.Api`: la API.
- `tests/CraneoBarbero.Api.Tests`: tests automáticos.
- `docs/DATABASE.md`: diagrama entidad-relación y diccionario de datos.

## Configuración

Las variables de entorno se cargan desde un archivo `.env` local (no se versiona) cuando la API corre en modo desarrollo. En producción se cargan desde el panel del proveedor. Los nombres están en `.env.example`.

| Variable | Uso |
|---|---|
| `PORT` | Puerto HTTP (opcional en desarrollo). |
| `FRONTEND_URL` | Origen del front permitido por CORS. Admite varios separados por coma. |
| `DATABASE_URL` | Cadena de conexión de PostgreSQL (formato URI o clave=valor). |
| `DATABASE_ROOT_CERT` | Ruta a otro certificado raíz de la base (opcional). |
| `JWT_SECRET` | Clave para firmar los tokens (mínimo 32 bytes). |
| `ADMIN_EMAIL`, `ADMIN_PASSWORD` | Admin inicial: se crea al arrancar solo si no hay ningún admin activo (contraseña de 12 caracteres o más). |
| `BREVO_API_KEY` | Clave de la API de Brevo para enviar los mails (de tipo API, no SMTP). |
| `EMAIL_FROM`, `EMAIL_FROM_NAME` | Remitente de los mails; la dirección tiene que estar verificada en Brevo. |
| `MP_ACCESS_TOKEN` | Access Token de Mercado Pago (de la cuenta vendedora de prueba). Sin esta variable las reservas funcionan pero no generan enlace de pago. |
| `MP_PUBLIC_KEY` | Public Key de Mercado Pago (la usa el front si hiciera falta). |
| `MP_WEBHOOK_SECRET` | Clave secreta de las notificaciones (webhooks) de Mercado Pago; sin ella se rechazan todas las notificaciones. |
| `MP_NOTIFICATION_URL` | URL del webhook (opcional). En Render se arma sola con `RENDER_EXTERNAL_URL` + `/payments/webhook`. |
| `GEMINI_API_KEY` | Clave de Google AI Studio para que el asistente entienda texto libre y para el reporte mensual con IA (opcional: sin ella el asistente funciona con botones y el reporte usa recomendaciones automáticas). |
| `MONTHLY_REPORT_EMAILS` | Destinatarios del reporte mensual automático, separados por coma (opcional: sin ella el reporte se genera pero no se envía). |

Los mails (verificación de email, recuperación de contraseña y avisos) se envían por la API HTTP de Brevo, porque el plan gratuito de Render bloquea los puertos SMTP. Se mandan en segundo plano y cada envío queda registrado en la tabla `Notifications`. Sin `BREVO_API_KEY` la API funciona igual, pero no envía mails.

La conexión a la base siempre verifica el certificado del servidor. Por defecto se usa el certificado raíz público de Supabase incluido en `src/CraneoBarbero.Api/certs`.

Las reglas del negocio (horario de atención, grilla de turnos, ventanas de cancelación y de reserva, vencimiento del pago, zona horaria y medios de pago) están en la sección `Business` de `appsettings.json`.

## Fechas y horarios

- La zona horaria del negocio es `America/Argentina/Buenos_Aires` (UTC−3). Todas las reglas de horario (franjas de atención, días, feriados, ventana de 60 días) se evalúan en esa zona.
- En la base y en la API los instantes se guardan y se devuelven en UTC (por ejemplo, un turno de las 09:00 de Argentina es `12:00Z`). Cuando la API devuelve un horario para mostrar, además agrega la hora local (`"time": "09:00"`). Los días (`date`) siempre son días de Argentina.

## Agenda y feriados

- Los horarios de cada barbero (bloques de 30 minutos del horario de atención) se generan solos hasta 12 meses hacia adelante, sin domingos ni feriados. El proceso corre al arrancar la API, todos los días después de las 00:30 y al crear o reactivar un barbero. Nunca vuelve a crear un horario que el admin eliminó.
- Los feriados nacionales se sincronizan desde la API pública de [ArgentinaDatos](https://argentinadatos.com/docs) (`https://api.argentinadatos.com/v1/feriados/{año}`, gratuita y sin cuenta), para el año en curso y el siguiente, al arrancar y cada 7 días. Se guardan los de tipo `inamovible` y `trasladable`; los `puente` (días no laborables con fines turísticos) no se cargan solos: los agrega el admin como día no laborable propio. Si la API no responde, se reintenta en la pasada siguiente y los feriados se pueden cargar a mano.
- Un feriado nuevo cierra los horarios libres de ese día y deja los que ya tienen turnos, para que el admin decida qué hacer con ellos.
- La configuración está en la sección `Schedule` de `appsettings.json` (URL de la API de feriados, intervalo de sincronización, hora de generación y días que se conservan los horarios pasados). `Schedule:JobsEnabled` apaga estos procesos; en desarrollo están apagados.

## Pagos

Los turnos online se pagan con Checkout Pro de Mercado Pago, siempre en **modo de prueba** (cuentas y tarjetas de prueba).

- Al reservar se crea una preferencia de pago que vence a los 30 minutos, igual que la reserva del horario. `POST /appointments/:id/pay` genera un enlace nuevo (anula el anterior).
- El turno se confirma solo cuando el servidor verifica el pago con la API de Mercado Pago: por la notificación `POST /payments/webhook` (con la firma `x-signature` validada con `MP_WEBHOOK_SECRET`) o cuando el cliente vuelve del checkout (`GET /payments/return`). Las dos vías son idempotentes.
- Si llega un pago aprobado para un turno que ya venció o se canceló, se reembolsa automáticamente.
- Las cancelaciones con reembolso lo piden a Mercado Pago (con clave de idempotencia); si Mercado Pago falla, la cancelación no se aplica y se informa el error.

## Tienda, precios, reportes, reseñas y asistente

- **Tienda y stock:** productos, categorías, proveedores, compras, ajustes y movimientos de stock. El stock solo cambia con movimientos y se descuenta recién cuando se aprueba el pago del pedido; cada venta guarda el costo vigente para calcular la ganancia.
- **Precios por inflación:** sugiere el porcentaje con la inflación del INDEC (`apis.datos.gob.ar`, con ArgentinaDatos de respaldo), muestra una vista previa, aplica el ajuste con redondeo y permite revertir el último. Todo cambio de precio o costo queda en el historial.
- **Imágenes:** `POST /images` (con sesión, multipart, JPG/PNG/WebP hasta 900 KB; el tipo se detecta por el contenido) guarda la foto en la tabla `Images` y devuelve `{ id, url }`; `GET /images/:id` es público, con caché larga. El front achica y recorta la foto en el navegador antes de subirla (mover y hacer zoom), así que no hace falta un servicio de archivos aparte.
- **Reportes:** ingresos, turnos, servicios, barberos, sucursales y productos por período; el admin los exporta a PDF y Excel (generados sin librerías externas). Todos los archivos se llaman `Reporte AAAA-MM - Craneo Barbero.pdf|xlsx` (o `Reporte AAAA-MM a AAAA-MM - Craneo Barbero` si el rango abarca varios meses) y adentro muestran el período como MM-AAAA.
- **Reporte mensual con IA:** `GET /reports/admin/ai-report` (mismos filtros, hasta dos años) arma un PDF con ocupación de la agenda por sucursal, día y franja, horarios más pedidos, barberos, servicios, productos y reseñas. Los números los calcula el servidor y se hace una sola consulta a Gemini para redactar el resumen y 2 a 4 estrategias, cada una con qué hacer, por qué (con el dato que la justifica), cómo aplicarla y qué medir (con reintento si Gemini está saturado). Sin Gemini, o pasados 10 reportes manuales en el día, usa recomendaciones automáticas. Además se genera solo el último día laboral de cada mes (sin domingos ni feriados), a partir de las 12:00, y se manda una vez a `MONTHLY_REPORT_EMAILS`; la tabla `MonthlyReports` evita repetirlo.
- **Reseñas:** el cliente califica una vez cada turno completado (barbero y servicio, de 1 a 5).
- **Asistente de reservas:** `POST /assistant/message`. Guía la reserva paso a paso con botones y entiende texto libre con un comparador local y, si falta algo, con Gemini (solo se le envía el texto del mensaje). Siempre confirma con un resumen y valida con las mismas reglas que la reserva normal.

## Base de datos

El esquema se crea y se actualiza solo con migraciones de EF Core; la API nunca modifica tablas al arrancar.

```
dotnet tool restore
dotnet ef database update --project src/CraneoBarbero.Api
```

Para crear una migración nueva: `dotnet ef migrations add <Nombre> --project src/CraneoBarbero.Api --output-dir Data/Migrations`.

El diagrama entidad-relación y el diccionario de datos están en [`docs/DATABASE.md`](docs/DATABASE.md).

### Respaldo y restauración

El plan gratuito de Supabase no hace copias de seguridad. Un repositorio **privado** aparte (`craneobarbero-backups`) corre todos los lunes una tarea de GitHub Actions que hace `pg_dump` del esquema `public`, lo cifra con AES-256 (clave guardada como secreto del repositorio) y lo deja como artefacto durante 30 días. También se puede correr a mano desde la pestaña Actions. Los volcados tienen datos personales: nunca se guardan en este repositorio.

Para restaurar (necesita el cliente de PostgreSQL 17 y la clave de cifrado):

1. Descargar el artefacto desde la ejecución elegida y descomprimir el `.zip`.
2. Descifrar: `openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -in craneobarbero-<fecha>.dump.enc -out craneobarbero.dump` (pide la clave).
3. Si es una base nueva, crear la extensión: `CREATE EXTENSION IF NOT EXISTS btree_gist WITH SCHEMA extensions;`
4. Restaurar: `pg_restore --clean --if-exists --no-owner --no-privileges -d "<cadena de conexión>" craneobarbero.dump`.
5. Si se restauró en una base nueva, apuntar `DATABASE_URL` (local y en Render) a esa base y comprobar `/health`.

### Importar datos del sistema anterior

Sobre una base vacía (con las migraciones aplicadas) se pueden importar sucursales, servicios, usuarios y turnos desde un respaldo de PostgreSQL en texto plano (`.sql` o `.sql.gz`). El respaldo contiene datos personales: nunca se guarda dentro del repositorio.

```
dotnet run --project src/CraneoBarbero.Api -- import-legacy <ruta del respaldo>
```

## Despliegue

La API corre en Render (plan gratuito) con Docker, a partir de `Dockerfile` y `render.yaml`:

- Servicio web en la región Oregón, la misma que la base de Supabase.
- `JWT_SECRET` lo genera Render; `DATABASE_URL`, `FRONTEND_URL`, `BREVO_API_KEY`, `EMAIL_FROM`, `EMAIL_FROM_NAME`, las variables de Mercado Pago y `GEMINI_API_KEY` se cargan en el panel del servicio (con "Save, rebuild, and deploy" para que se apliquen).
- GitHub Actions compila y corre los tests en cada push a `main` y a las ramas de trabajo (`seba-branch`, `rafa-branch`, `gera-branch`) y en cada pull request (`.github/workflows/ci.yml`). Render solo despliega un commit de `main` cuando esos chequeos pasan (`autoDeployTrigger: checksPass`).
- Render controla `/health`, que también consulta la base. Un monitor externo lo llama cada pocos minutos para que el servicio no se duerma.
- El front en Vercel apunta a esta API con `VITE_API_URL`.

## Monitoreo y errores

- **Disponibilidad:** UptimeRobot (plan gratuito) llama a `/health` cada 5 minutos y avisa por mail si no responde. Ese pedido además mantiene despierto el servicio de Render (que se duerme a los 15 minutos sin tráfico, y con él los procesos automáticos, los recordatorios y los webhooks) y, como `/health` hace una consulta a la base, genera actividad en Supabase, que pausa los proyectos gratuitos sin uso durante 7 días.
- **Errores:** los errores no controlados se registran con Serilog (en producción en formato JSON, sin cuerpos ni datos de las consultas) y se ven en los logs de Render. Al cliente solo le llega un mensaje genérico con la forma `{ error, code }`.
- **Front:** una pantalla de error atrapa las fallas de una vista y ofrece recargar, en lugar de dejar la página en blanco.

## Seguridad y datos personales

- Contraseñas con bcrypt, sesiones con JWT, permisos por rol y por dueño del recurso validados en el servidor, CORS limitado al front, límite de pedidos por IP (más estricto en `/auth`), cabeceras de seguridad, HSTS y conexión a la base con certificado verificado.
- Ley 25.326: el registro exige aceptar los términos y la política de privacidad (se guarda la fecha en `Users.termsAcceptedAt`). El cliente puede ver y corregir sus datos, desactivar su cuenta o eliminarla; al eliminarla, sus turnos y compras pasados quedan anonimizados para no alterar los reportes.
- Las dependencias se revisan con `dotnet list package --vulnerable --include-transitive` (en el front, `npm audit`).

## Ejecutar

```
dotnet run --project src/CraneoBarbero.Api
```

En desarrollo, la documentación de la API está en `/scalar/v1`.

## Tests

```
dotnet test
```

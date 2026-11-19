# Cráneo Barbero — Backend

API de la barbería Cráneo Barbero: cuentas, turnos, pagos, productos y reportes.

Hecha con ASP.NET Core (.NET 10) sobre PostgreSQL. Se despliega en Render con Docker.

## Estructura

- `src/CraneoBarbero.Api`: la API.
- `tests/CraneoBarbero.Api.Tests`: tests automáticos.

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

## Base de datos

El esquema se crea y se actualiza solo con migraciones de EF Core; la API nunca modifica tablas al arrancar.

```
dotnet tool restore
dotnet ef database update --project src/CraneoBarbero.Api
```

Para crear una migración nueva: `dotnet ef migrations add <Nombre> --project src/CraneoBarbero.Api --output-dir Data/Migrations`.

### Importar datos del sistema anterior

Sobre una base vacía (con las migraciones aplicadas) se pueden importar sucursales, servicios, usuarios y turnos desde un respaldo de PostgreSQL en texto plano (`.sql` o `.sql.gz`). El respaldo contiene datos personales: nunca se guarda dentro del repositorio.

```
dotnet run --project src/CraneoBarbero.Api -- import-legacy <ruta del respaldo>
```

## Despliegue

La API corre en Render (plan gratuito) con Docker, a partir de `Dockerfile` y `render.yaml`:

- Servicio web en la región Oregón, la misma que la base de Supabase.
- `JWT_SECRET` lo genera Render; `DATABASE_URL`, `FRONTEND_URL`, `BREVO_API_KEY`, `EMAIL_FROM` y `EMAIL_FROM_NAME` se cargan en el panel del servicio.
- GitHub Actions compila y corre los tests en cada push a `main` o `seba-branch` y en cada pull request (`.github/workflows/ci.yml`). Render solo despliega un commit de `main` cuando esos chequeos pasan (`autoDeployTrigger: checksPass`).
- Render controla `/health`, que también consulta la base. Un monitor externo lo llama cada pocos minutos para que el servicio no se duerma.
- El front en Vercel apunta a esta API con `VITE_API_URL`.

## Ejecutar

```
dotnet run --project src/CraneoBarbero.Api
```

En desarrollo, la documentación de la API está en `/scalar/v1`.

## Tests

```
dotnet test
```

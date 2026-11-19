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

La conexión a la base siempre verifica el certificado del servidor. Por defecto se usa el certificado raíz público de Supabase incluido en `src/CraneoBarbero.Api/certs`.

Las reglas del negocio (horario de atención, grilla de turnos, ventanas de cancelación y de reserva, vencimiento del pago, zona horaria y medios de pago) están en la sección `Business` de `appsettings.json`.

## Ejecutar

```
dotnet run --project src/CraneoBarbero.Api
```

En desarrollo, la documentación de la API está en `/scalar/v1`.

## Tests

```
dotnet test
```

# Seguridad — 5 amenazas identificadas

| # | Amenaza | Cómo se mitiga en MiniCore |
|---|---|---|
| 1 | **Acceso a cuentas ajenas (BOLA / IDOR)**: un cliente cambia el `sourceAccountId` o consulta `/accounts/{id}` de otro. | El servidor compara el dueño de la cuenta (`OwnerId`) con el `sub` del token validado, y **nunca** confía en un identificador de cliente enviado en el body. Los roles Auditor/Operator tienen permisos mínimos y explícitos. Cubierto por el escenario E11. |
| 2 | **Token falsificado o mal validado** (firma débil, `alg: none`, token de otro emisor, expirado). | JWT validado por completo: firma, algoritmo permitido (solo HS256), emisor, audiencia y expiración (tolerancia de 30 s). La clave de firma viene de configuración (variable de entorno o gestor de secretos), nunca del repositorio, y en producción la API no arranca sin ella. |
| 3 | **Inyección SQL**. | Todas las consultas son parametrizadas (Dapper). La única consulta construida dinámicamente (filtros de fechas del historial) añade solo fragmentos fijos escritos en el código; los valores van siempre como parámetros. Los montos y cuentas se validan antes de tocar la base de datos. |
| 4 | **Doble ejecución y condiciones de carrera** (reintentos del cliente, requests simultáneos, saldo negativo, dinero duplicado). | Idempotency-Key obligatoria con llave primaria `(ClientId, Key)` reservada en la misma transacción; bloqueo `UPDLOCK` de las cuentas en orden fijo; una transacción atómica; y `CHECK (Balance >= 0)` en la base como última red de seguridad. Cubierto por E9, E10 y E12. |
| 5 | **Fuga de información por errores y logs**, y manipulación de logs. | Errores en `ProblemDetails` sin stack traces ni mensajes internos (el detalle va solo al log, con el `correlationId`). Los logs no incluyen tokens, claves ni datos personales. El `X-Correlation-Id` recibido se acepta solo si es corto y alfanumérico, para evitar inyección de saltos de línea en los logs. |

**Controles adicionales:** cuerpo de petición limitado a 16 KB, `size` máximo de 100 y tope de página (anti-abuso); cabecera `X-Content-Type-Options: nosniff`.

> **⚠ Advertencia de despliegue.** El emisor de tokens simulado (`/dev/token/{user}`, que entrega a cualquiera un token de `OPERATOR` o `AUDITOR`) y la inyección de fallos (`X-Simulate-Failure`) existen **solo cuando `ASPNETCORE_ENVIRONMENT=Development`**. **Nunca deben desplegarse así.** En cualquier otro ambiente esos dos puntos no existen y la API **no arranca sin `Jwt:SigningKey`** (verificado). La única barrera es esa variable de entorno: debe fijarse explícitamente en cada despliegue.

**No cubierto:**
* **Enumeración de cuentas:** en saldo y movimientos, una cuenta inexistente responde 404 y una ajena 403, así que un usuario autenticado puede deducir qué cuentas existen. Es una exigencia del escenario E7 (404 para cuenta inexistente); se acepta ese compromiso. En `POST /api/transfers` la propiedad del origen se valida antes de revelar si el destino existe.
* **HTTPS/HSTS:** no se configuran en la API; se asume TLS terminado en el gateway/proxy inverso.
* **Desbordamiento numérico:** un monto cercano al tope técnico (1 000 000 000 000,00) podría desbordar el saldo `DECIMAL(18,2)` del destino. Resultado: error 500 genérico con **rollback completo, sin corrupción de datos** (la transacción es atómica y `CHECK (Balance >= 0)` sigue vigente).
* Pendiente además: rate limiting, rotación automática de claves e inmutabilidad de la auditoría.

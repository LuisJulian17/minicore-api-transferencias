# MiniCore · API de Transferencias

API REST de transferencias entre cuentas para **MiniCore Bank** (banco ficticio), construida con **.NET 8**, **Dapper** y **SQL Server**. Cada transferencia es atómica, idempotente y segura frente a concurrencia.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![SQL Server](https://img.shields.io/badge/SQL%20Server-LocalDB-CC2927)
![Auth](https://img.shields.io/badge/Auth-JWT%20Bearer-black)
![Tests](https://img.shields.io/badge/tests-xUnit-green)

---

## Contenido

1. [Qué hace](#1-qué-hace)
2. [Cómo ejecutarlo](#2-cómo-ejecutarlo)
3. [Estructura del repositorio](#3-estructura-del-repositorio)
4. [Endpoints](#4-endpoints)
5. [Reglas de negocio](#5-reglas-de-negocio)
6. [Decisiones de diseño](#6-decisiones-de-diseño)
7. [Supuestos documentados](#7-supuestos-documentados)
8. [Limitaciones conocidas](#8-limitaciones-conocidas)
9. [Respuestas breves](#9-respuestas-breves)
10. [Documentación adicional](#10-documentación-adicional)

---

## 1. Qué hace

- Transfiere dinero entre cuentas con **comisión**, **límite diario** y validación de estado de cuenta.
- Garantiza que **nunca se mueve dinero a medias**: débito, crédito, comisión y auditoría ocurren en una sola transacción.
- Soporta **reintentos seguros** mediante `Idempotency-Key`.
- Permite a un **operador reversar** una transferencia dentro de 24 horas.
- Autenticación con **JWT** y autorización por rol (`Customer`, `Operator`, `Auditor`).
- Errores en formato **ProblemDetails** con `code` estable y `correlationId` para trazabilidad.

**Stack:** .NET 8 · ASP.NET Core (Controllers) · Dapper 2.1.35 · Microsoft.Data.SqlClient 5.2.2 · SQL Server · JWT Bearer · xUnit + `WebApplicationFactory`.

---

## 2. Cómo ejecutarlo

### Requisitos

- [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server LocalDB (viene con Visual Studio) o cualquier instancia de SQL Server
- `sqlcmd` en el PATH

### Paso a paso

**1. Crear la base de datos y los datos semilla**

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -i db\01_schema.sql
sqlcmd -S "(localdb)\MSSQLLocalDB" -f 65001 -i db\02_seed_and_reset.sql
```

> `02_seed_and_reset.sql` se puede ejecutar cuantas veces se quiera: borra los datos y deja las 6 cuentas semilla en su estado inicial. Sirve para repetir cualquier escenario.

**2. Levantar la API**

```powershell
dotnet run --project src/MiniCore.Api
```

La API queda en `http://localhost:5080` (perfil de **Development**).

**3. Obtener un token (solo Development)**

```powershell
curl http://localhost:5080/dev/token/CUST-01
```

Usuarios disponibles: `CUST-01` … `CUST-06`, `OPERATOR` y `AUDITOR`.

**4. Probar**

Abre `requests/minicore.http` en Visual Studio / VS Code (extensión REST Client) y ejecuta los casos. Están cubiertos los escenarios E1–E12.

**5. Correr las pruebas**

```powershell
dotnet test
```

Las pruebas de integración usan la base `MiniCore` en LocalDB y la reinician antes de cada prueba. Para apuntar a otra instancia:

```powershell
$env:MINICORE_TEST_CONNECTION = "Server=...;Database=MiniCore;..."
```

### Configuración

| Variable de entorno | Para qué sirve |
|---|---|
| `ConnectionStrings__MiniCore` | Cadena de conexión a SQL Server |
| `Jwt__SigningKey` | Clave de firma HS256 (obligatoria fuera de Development) |

> **Importante:** los endpoints `/dev/token/*` y el inyector de fallas **solo existen en el entorno Development** y nunca deben desplegarse.

### Cuentas semilla

| Cuenta | Titular | Estado | Saldo (USD) | Uso |
|---|---|---|---:|---|
| `ACC-1001` | Ana Torres | Active | 10,000.00 | Cuenta principal de pruebas |
| `ACC-1002` | Luis Pérez | Active | 250.00 | Escenario de concurrencia |
| `ACC-1003` | Carmen Ruiz | Blocked | 5,000.00 | Cuenta bloqueada |
| `ACC-1004` | Empresa XYZ | Active | 120,000.00 | Límite diario |
| `ACC-1005` | Cuenta histórica | Closed | 0.00 | Cuenta cerrada |
| `ACC-1006` | Pedro Gil | Active | 501.00 | Saldo vs. comisión |

---

## 3. Estructura del repositorio

```text
MiniCore/
├── db/
│   ├── 01_schema.sql              # Tablas, llaves, restricciones
│   └── 02_seed_and_reset.sql      # Datos semilla + reinicio
├── src/
│   ├── MiniCore.Domain/           # Reglas puras (sin I/O): comisión, límites, ventanas
│   ├── MiniCore.Infrastructure/   # Dapper, transacciones, idempotencia, consultas
│   └── MiniCore.Api/              # Controllers, JWT, middleware, manejo de errores
├── tests/
│   └── MiniCore.Tests/            # Unitarias + integración (E1–E12)
├── requests/
│   └── minicore.http              # Colección de peticiones de ejemplo
├── docs/
│   └── SECURITY.md                # Amenazas y mitigaciones
├── scripts/
│   └── setup.ps1                  # Preparación de la base de datos
├── global.json                    # Versión fija del SDK
├── USO_IA.md                      # Declaración de uso de IA
└── README.md
```

La dependencia fluye en un solo sentido: `Api → Infrastructure → Domain`. El dominio no conoce HTTP ni SQL, por eso sus reglas se prueban sin base de datos.

---

## 4. Endpoints

Todas las rutas requieren `Authorization: Bearer <token>`.

| Método | Ruta | Descripción | Roles |
|---|---|---|---|
| `POST` | `/api/transfers` | Crea una transferencia | `Customer` |
| `GET` | `/api/transfers/{id}` | Consulta una transferencia | `Customer` (propia), `Operator`, `Auditor` |
| `POST` | `/api/transfers/{id}/reverse` | Reversa una transferencia | `Operator` |
| `GET` | `/api/accounts/{id}/movements` | Movimientos de una cuenta | `Customer` (propia), `Auditor` |
| `GET` | `/api/accounts/{id}/balance` | Saldo de una cuenta | `Customer` (propia), `Auditor` |

### Crear una transferencia

```http
POST /api/transfers
Authorization: Bearer <token>
Idempotency-Key: 7b1f9c9e-3c0f-4d55-9d1e-2f6a0f0f8a11
Content-Type: application/json

{
  "sourceAccountId": "ACC-1001",
  "destinationAccountId": "ACC-1002",
  "amount": 1000.00,
  "currency": "USD",
  "description": "Pago de arriendo"
}
```

**Respuesta `201 Created`**

```json
{
  "transferId": "0d0a6a43-6a39-4a8f-9d57-5f1f0f2f6d1e",
  "status": "Completed",
  "amount": 1000.00,
  "fee": 5.00,
  "totalDebited": 1005.00,
  "sourceBalanceAfter": 8995.00,
  "createdAtUtc": "2026-10-13T14:05:00Z"
}
```

### Cabeceras relevantes

| Cabecera | Sentido | Descripción |
|---|---|---|
| `Idempotency-Key` | Petición | Obligatoria en `POST`. La genera el **cliente** (un GUID) y la reutiliza en cada reintento. Máximo 100 caracteres. |
| `Idempotent-Replayed: true` | Respuesta | Indica que se devolvió la respuesta guardada, sin repetir el efecto. |
| `X-Correlation-Id` | Ambos | Se acepta si es seguro (≤ 64 caracteres, `[A-Za-z0-9-_.]`); si no, se genera uno. |

### Formato de error

```json
{
  "type": "about:blank",
  "title": "Saldo insuficiente",
  "status": 422,
  "code": "INSUFFICIENT_FUNDS",
  "correlationId": "c1f6a2e0-..."
}
```

### Códigos de error

| HTTP | `code` | Cuándo ocurre |
|---:|---|---|
| 400 | `MISSING_IDEMPOTENCY_KEY` | Falta la cabecera `Idempotency-Key` |
| 400 | `INVALID_REQUEST` | JSON mal formado, cuerpo vacío, tipos inválidos o clave de más de 100 caracteres |
| 401 | — | Sin token o token inválido |
| 403 | — | Rol o propiedad de la cuenta insuficiente |
| 404 | `ACCOUNT_NOT_FOUND` / `TRANSFER_NOT_FOUND` | El recurso no existe |
| 409 | `TRANSFER_ALREADY_REVERSED` | La transferencia ya fue reversada |
| 409 | `REVERSAL_INSUFFICIENT_FUNDS` | El destino no puede devolver el dinero |
| 422 | `INVALID_AMOUNT` | Monto ≤ 0, más de 2 decimales o sobre el tope |
| 422 | `INVALID_CURRENCY` | Moneda distinta de USD |
| 422 | `SAME_ACCOUNT` | Origen y destino son la misma cuenta |
| 422 | `ACCOUNT_NOT_ACTIVE` | Origen o destino bloqueado o cerrado |
| 422 | `INSUFFICIENT_FUNDS` | El saldo no cubre monto + comisión |
| 422 | `DAILY_LIMIT_EXCEEDED` | Se supera el límite diario de 5,000.00 |
| 422 | `IDEMPOTENCY_KEY_REUSED` | Misma clave con un cuerpo distinto |
| 422 | `REVERSAL_WINDOW_EXPIRED` | Pasaron más de 24 horas |
| 500 | `INTERNAL_ERROR` | Error inesperado (sin detalles internos en la respuesta) |

---

## 5. Reglas de negocio

### Comisión

| Monto | Comisión |
|---|---|
| Hasta 500.00 (inclusive) | 0.00 |
| Mayor a 500.00 | `max(3.00, 0.5 % del monto)` redondeada a 2 decimales |

| Monto | 0.5 % | Comisión |
|---:|---:|---:|
| 100.00 | — | **0.00** |
| 501.00 | 2.505 | **3.00** (aplica el mínimo) |
| 1,000.00 | 5.00 | **5.00** |
| 1,001.00 | 5.005 | **5.01** (redondeo comercial) |
| 1,234.57 | 6.17285 | **6.17** |

### Límite diario

- **5,000.00** por cuenta de origen, **inclusivo** (llegar exactamente a 5,000.00 está permitido).
- Se suman los **montos sin comisiones** de las transferencias `Completed`.
- El día es el **día calendario UTC**: intervalo `[00:00:00, 24:00:00)`.
- Una transferencia reversada **no cuenta**: el reverso libera cupo.

### Reverso

- Solo el rol `Operator`.
- **Una sola vez** por transferencia (si se repite: `409`).
- Dentro de **24 horas desde la creación**, inclusive (justo a las 24 h aún es válido).
- Devuelve **monto + comisión** a la cuenta de origen y debita el monto del destino.
- Si el destino ya no tiene fondos para devolver: `409 REVERSAL_INSUFFICIENT_FUNDS`.

---

## 6. Decisiones de diseño

| Tema | Decisión | Por qué |
|---|---|---|
| Acceso a datos | **Dapper** con SQL explícito | Control total de bloqueos y transacciones; el SQL es auditable y no hay magia de ORM. |
| Atomicidad | **Una transacción por petición** | Débito, crédito, comisión, movimientos y auditoría se confirman juntos o no se confirma nada (E12). |
| Concurrencia | `UPDLOCK, ROWLOCK` sobre ambas cuentas | Dos peticiones no pueden leer el mismo saldo y sobregirar la cuenta (E10). |
| Deadlocks | Bloqueo en **orden fijo** (`Id` ordinal) | Transferencias cruzadas A→B y B→A bloquean en el mismo orden y no se enredan. |
| Reglas | **C# puro** en `Domain` | Probables sin base de datos, sin lógica escondida en procedimientos almacenados. |
| Idempotencia | `INSERT` con PK `(ClientId, [Key])` **dentro de la misma transacción** | La propia llave primaria es el candado: el segundo intento falla con 2627/2601 y se trata como repetición. |
| Integridad de la clave | `RequestHash` = SHA-256 del contenido | Misma clave con otro contenido → `IDEMPOTENCY_KEY_REUSED`. |
| Dinero | `decimal` / `DECIMAL(18,2)` y `MidpointRounding.AwayFromZero` | Sin errores de coma flotante; redondeo comercial (1,001.00 → comisión 5.01). |
| Fechas | `TimeProvider` inyectable y `DateTime` en UTC | Las pruebas avanzan el reloj sin esperar 24 h reales. |
| Dapper y UTC | `UtcDateTimeHandler` registrado una sola vez | SQL Server devuelve `DateTime` sin zona; el handler fuerza `Kind=Utc` para serializar con `Z`. |
| Errores | **ProblemDetails** con `code` y `correlationId` | Contrato estable para clientes y trazabilidad en logs. |
| Fallas simuladas | `ITransferFaultInjector` (objeto nulo en producción) | Permite probar el rollback entre débito y crédito sin ensuciar el código de negocio. |
| Observabilidad | Logs JSON estructurados con `correlationId` | Se puede seguir una petición de punta a punta. |

---

## 7. Supuestos documentados

1. **Moneda.** Solo `USD`. Si el cuerpo no trae moneda, se asume `USD`; cualquier otra se rechaza.
2. **Día del límite diario.** Día calendario **UTC** (así lo exige el enunciado). Se suman montos sin comisiones de transferencias `Completed`.
3. **Redondeo.** `decimal.Round(…, 2, MidpointRounding.AwayFromZero)`. El redondeo bancario daría 5.00 en lugar de 5.01 para 1,001.00.
4. **Destino de la comisión.** Se debita del origen y **no se acredita a ninguna cuenta**, porque el enunciado no define una cuenta de ingresos. Queda registrada en la transferencia y en los movimientos.
5. **Validación de monto.** Debe ser > 0, tener máximo 2 decimales y no superar 1,000,000,000,000.00.
6. **Orden de validación.** Origen existe (404) → origen pertenece al usuario (403) → destino existe (404) → estado de ambas cuentas → fondos → límite diario.
7. **Idempotencia.**
   - La clave la genera el cliente; el `ClientId` sale del claim `sub` del JWT, de modo que dos clientes pueden usar la misma clave sin chocar.
   - Solo se guardan respuestas **exitosas**; los errores de negocio se reevalúan en cada intento (el saldo pudo cambiar).
   - Las claves no expiran.
8. **Entradas inválidas.** JSON mal formado, cuerpo vacío, tipos inválidos o `Idempotency-Key` de más de 100 caracteres devuelven `400 INVALID_REQUEST`.
9. **Autorización.**
   - `Customer` solo transfiere desde cuentas propias; el dueño sale del token, nunca del cuerpo.
   - `Auditor` lee todo (cuentas y transferencias) y no puede transferir.
   - `Operator` lee transferencias y reversa, pero no consulta cuentas.
10. **Enumeración.** Distinguir `404` (no existe) de `403` (no es tuya) permite saber si una cuenta existe. Se acepta porque los escenarios E7 exigen devolver 404 para cuentas inexistentes.
11. **Entorno.** Se asume HTTPS/HSTS terminado en el gateway. Los endpoints `/dev/*` y el inyector de fallas existen solo en Development.

---

## 8. Limitaciones conocidas

- **Sin rate limiting.** Un cliente autenticado podría saturar la API; se delega al gateway.
- **Claves de idempotencia sin expiración.** La tabla crece indefinidamente; en producción haría falta una política de purga.
- **Auditoría no inmutable.** La tabla `AuditLog` no está protegida contra alteración por alguien con acceso directo a la base.
- **Comisión sin contrapartida contable.** No hay cuenta de ingresos del banco; se debita pero no se acredita.
- **Una sola moneda.** No hay conversión ni cuentas multi-moneda.
- **Enumeración por 404.** Ver supuesto 10.
- **Tokens de desarrollo.** Los endpoints `/dev/token/*` no deben llegar nunca a un entorno real.

---

## 9. Respuestas breves

**Si tuviera una semana más**
Implementaría una cuenta contable de ingresos para la comisión, purga de claves de idempotencia, auditoría inmutable (append-only con hash encadenado), rate limiting y pruebas de carga automatizadas en el pipeline.

**¿Cómo manejaría 500 TPS?**
Primero mediría. El cuello de botella natural son los bloqueos sobre cuentas "calientes", no el CPU. Aplicaría: índices revisados con los planes de ejecución, transacciones lo más cortas posible, `READ_COMMITTED_SNAPSHOT` para las lecturas, pool de conexiones dimensionado, y escalado horizontal de la API (es *stateless*). Si una sola cuenta concentra mucho tráfico, evaluaría particionar o encolar sus operaciones.

**¿Cuál es el mayor riesgo del diseño actual?**
La contención de bloqueos en cuentas muy activas y el crecimiento sin control de la tabla de idempotencia. Ambos son medibles y tienen solución conocida, pero hoy no están mitigados.

**¿Y si hubiera varias monedas?**
La moneda pasaría a ser parte de la cuenta, la transferencia exigiría que ambas coincidan (o aplicaría una tasa de cambio versionada y auditada), y el límite diario se evaluaría en una moneda de referencia. El `decimal(18,2)` tendría que revisarse por moneda (algunas usan 0 o 3 decimales).

---

## 10. Documentación adicional

- 🔐 [`docs/SECURITY.md`](docs/SECURITY.md): amenazas principales y mitigaciones.
- 🤖 [`USO_IA.md`](USO_IA.md): declaración del uso de herramientas de IA en este trabajo.
- 🧪 [`requests/minicore.http`](requests/minicore.http): colección de peticiones de ejemplo.

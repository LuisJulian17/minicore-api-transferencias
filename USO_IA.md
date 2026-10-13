# Declaración de uso de IA

Declaro que utilicé herramientas de inteligencia artificial durante esta prueba, tal como lo permiten las reglas. Esta declaración describe qué usé, para qué, qué hice yo y cómo verifiqué el resultado.

## 1. Herramientas utilizadas

| Herramienta | Uso |
|---|---|
| **Claude** (chat, Anthropic) | Análisis de los enunciados, diseño de la solución, revisión de reglas de negocio, apoyo en redacción y en documentación. |
| **Claude Code** (CLI, Anthropic) | Ejecución de compilación y pruebas, y revisión final de mi código. |

## 2. Para qué la usé

- **Diseño y revisión de la API.** La usé para identificar errores, reglas mal interpretadas y cosas faltantes (orden de validaciones, manejo de entradas mal formadas, límites de longitud de la clave de idempotencia, concurrencia en las réplicas de una misma clave).
- **Pruebas.** Me ayudó a crear las pruebas existentes y a mapear los escenarios E1–E12 del enunciado, para que no se me pasara ninguno. Las pruebas corren contra una base de datos real.
- **Documentación.** Me ayudó a armar el `README.md` y el `docs/SECURITY.md`. Yo aporté lo que identifiqué, y le pedí que evaluara otros riesgos que yo no hubiera considerado.
- **Script de preparación (`scripts/setup.ps1`).** Lo usé para que el script no omitiera ningún paso.
- **Redacción.** Le pedí que me ayudara a redactar mejor mis respuestas escritas (preguntas técnicas y casos de liderazgo). Las ideas, las posturas y las experiencias que menciono son mías.
- **Aprendizaje.** Le pedí explicaciones línea por línea de las partes que no dominaba (redondeo `AwayFromZero`, ventana UTC del límite diario, `UtcDateTimeHandler`, inyección de fallas, tabla de idempotencia) hasta poder explicarlas.

## 3. Cómo trabajé con el código

 Al terminar, pedí a Claude Code que **validará mi codigo**, ejecutara la compilación y las pruebas, y me señalara errores.


## 4. Qué hice yo

- Escribí el código del repositorio, entendiendo la razón de cada decisión.
- Tomé o validé las decisiones de diseño: una transacción por petición, bloqueo en orden fijo de cuentas, idempotencia dentro de la misma transacción, `decimal` y redondeo comercial, día UTC para el límite.
- Revisé y ajusté el README y el SECURITY.md para que reflejen lo que realmente hace el sistema y lo que no.
- Ejecuté la compilación y las pruebas, y corregí lo que fallaba.
- Estoy en capacidad de explicar cualquier parte del código y de justificar por qué está así.

## 5. Cómo verifiqué el resultado

- Compilación sin errores ni advertencias.
- Suite de pruebas (unitarias e integración) en verde, cubriendo los escenarios E1–E12.
- Pruebas de concurrencia: transferencias cruzadas sin deadlocks y réplicas simultáneas de la misma clave ejecutadas una sola vez.
- Revisión final con Claude Code.
- Contraste de las reglas contra el enunciado original (por ejemplo, el día UTC y la ventana de 24 horas del reverso).

## 6. Límites y responsabilidad

- La IA pudo equivocarse o interpretar mal una regla. Por eso contrasté sus propuestas con el enunciado y con pruebas, y descarté o corregí lo que no cuadraba.
- Asumo la responsabilidad total por el contenido entregado.

## 7. Qué haría distinto sin IA

Habría tardado bastante más en la documentación y en cubrir casos borde de concurrencia y de entradas inválidas. El diseño central y mi capacidad de explicarlo no dependen de la herramienta.
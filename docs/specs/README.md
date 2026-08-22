# Specs de ReviewMemory

| Spec | Contenido | Estado |
|---|---|---|
| [01-vision](01-vision.md) | problema, posicionamiento, no-goals, objetivo por etapa, señales de éxito | escrita (PR #6) |
| [02-arquitectura](02-arquitectura.md) | stack, estructura de la solución, modelo de dominio vs tablas, flujo de ingesta, limitaciones | escrita (PR #3) |
| [03-recuperacion](03-recuperacion.md) | señales de ranking con valores exactos, semántica search vs context, formatos console/json, contrato para agentes | escrita (PR #5) |
| [04-cli](04-cli.md) | comandos, opciones y defaults literales, exit codes, mensajes de error, ejemplos verificados | escrita (PR #4) |
| [05-decisiones](05-decisiones.md) | catálogo de señales léxicas, orden de evaluación, restricciones de BD, aprendizaje post-review | escrita (PR #1) |
| [06-roadmap](06-roadmap.md) | GraphQL resolved, índice incremental, MCP server, skills, pgvector, aprendizaje — compromiso vs propuesta | escrita (PR #2) |

Las specs documentan el comportamiento implementado en `src/` y definen los
criterios de aceptación de la siguiente iteración de cada área. Regla de la
serie: si una spec difiere del código, gana el código — y el PR que cambia uno
obliga a cambiar la otra.

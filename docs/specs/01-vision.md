# Visión

## Problema

Cada vez se escribe más código con agentes y, en consecuencia, cada vez se revisa más. Generar otro diff o otra lista de findings es barato y abundante. El recurso escaso es otro: conocer el codebase y las decisiones que el equipo ya tomó sobre él.

Ese conocimiento existe pero está enterrado. Quedó escrito en los hilos de review de cientos de PRs mergeados: patrones rechazados con su motivo, excepciones aceptadas con condiciones, bugs recurrentes en ciertos módulos. Hoy nadie lo consulta antes de revisar. Resultado previsible: cada reviewer —humano o agente— vuelve a preguntar lo que ya estaba respondido, reaprueba lo que fue rechazado o rechaza lo que fue debatido y aceptado.

## Qué es

ReviewMemory indexa los PRs de un repositorio y sus discusiones de review en PostgreSQL. Antes de revisar un PR, cualquier reviewer consulta el historial:

```
reviewmemory context Shirka-Corporation/player-manager --pr 2268
```

```
3 discusión(es) históricamente relevante(s)

1. HIGH — Provider callbacks could be processed twice.
   Similitud: 0.91
   PR #1943 (Shirka-Corporation/player-manager) · 2026-03-10
   File: src/AdJoePayoutHandler.cs:120

   Preocupación previa del reviewer:
   Provider callbacks could be processed twice during retries...

   Resolución: Finding aceptado — se corrigió la implementación
   https://github.com/Shirka-Corporation/player-manager/pull/1943#discussion_r…
```

Cada resultado trae lo que un reviewer necesita: la preocupación textual del reviewer anterior, dónde se planteó, cómo terminó (aceptada, rechazada, parcialmente aceptada o desconocido) y el enlace al hilo original.

## Posicionamiento

No es otro AI reviewer. La memoria es el producto; el LLM reviewer no lo es.

| | AI reviewer genérico | ReviewMemory |
|---|---|---|
| Producto | el modelo que genera findings | la memoria que los contextualiza |
| Criterio | reglas genéricas del prompt | decisiones reales de este equipo |
| Defensa | ninguna: lo reemplaza el próximo modelo | acumulativa: crece con cada review |

Es deliberadamente agnóstica del reviewer: sirve igual para una persona, para Codex, para Claude o para Copilot. Hoy se integra por CLI (`--format json` para agentes); después, vía MCP server (spec 06-roadmap).

## Qué NO hace

No-goals explícitos; proponer cualquiera de estos exige cambiar esta spec primero:

- **No comenta PRs** ni abre reviews en GitHub. Solo lee el historial y responde consultas.
- **No puntúa código** ni emite findings propios sobre el diff actual. Clasifica precedentes históricos, no calidad.
- **No reemplaza al reviewer**: no aprueba ni bloquea. Humano o agente decide; esto solo aporta contexto.
- **No entrena ni aloja modelos**: la recuperación de etapa 1 no usa IA, solo FTS, solapamiento de archivos y recencia.
- **No modifica el repositorio fuente**: es un índice de lectura sobre GitHub.

## Usuario objetivo y momento de uso

Dos usuarios, mismo momento:

- **Reviewer humano**: ejecuta `context` sobre el PR antes de leer el diff, para saber qué preocupaciones aplican y cuáles ya fueron descartadas por el equipo.
- **Agente reviewer** (Codex, Claude, Copilot u otro): consume `search`/`context` como parte de su contexto al iniciar la review, hoy invocando el CLI con `--format json`, mañana vía MCP.

El momento es antes y durante una review concreta. No después (no es una herramienta de post-mortem) ni como gate automático de CI.

## Objetivo por etapa

### Etapa 1 — bases (hecha)

Verificable contra el README y el código actual:

- Se construyó la solución `Cli · Core · GitHub · Storage` con tests (xUnit) y CI que compila y corre los tests contra Postgres real.
- Se implementó la ingesta por REST de GitHub: PRs, archivos tocados, hunks y comentarios agrupados en hilos.
- Se creó el esquema PostgreSQL con migraciones embebidas versionadas en `schema_migrations`.
- Se implementó el ranking sin IA con tres señales combinadas —FTS sobre comentario y ruta (`tsvector`, índice GIN), solapamiento de archivos y recencia con vida media de 120 días—, pesos 0.55/0.30/0.15 y bandas HIGH ≥ 0.65, MEDIUM ≥ 0.40 (ver spec 03-recuperacion).
- Se infirió la decisión de cada hilo (`accepted`/`rejected`/`partially_accepted`/`unknown`) con señales léxicas deterministas; señales contradictorias quedan como `unknown` en vez de adivinar (ver spec 05-decisiones).
- Se publicaron los comandos `index`, `search` y `context` con salida `console` y `json` equivalentes; `context` excluye las discusiones del propio PR (ver spec 04-cli).

Quedó fuera a propósito: el estado real de resolución de hilos (la API REST no lo expone), el re-indexado incremental y cualquier integración con agentes.

### Etapa 2 — endurecer lo existente y cerrar las brechas

- Endurecer las specs 03-recuperacion, 04-cli y 05-decisiones: convertir el comportamiento implementado en contrato verificable (formato de salida estable, semántica del score, reglas de decisión con tests de contrato).
- Resolver el estado `resolved` de los hilos vía GraphQL y distinguirlo del desenlace inferido léxicamente.
- Índice incremental: re-indexar un PR actualiza sus hilos en lugar de borrarlos y recrearlos.
- El MCP server, embeddings/pgvector y el aprendizaje post-review quedan planificados en la spec 06-roadmap; esta etapa no los compromete.

## Señal de éxito

Medible con datos propios, sin terceros:

- **Cobertura de precedentes**: porcentaje de los comentarios de una review nueva que ya tenían precedente recuperable (hit en banda HIGH o MEDIUM) en la memoria. Si la memoria funciona, los findings repetibles deberían aparecer con precedente; los genuinamente nuevos, no.
- **Recall@k manual sobre reviews pasadas**: para una muestra de PRs ya revisados y mergeados, ejecutar `context` (que ya excluye los hilos del propio PR) y verificar si las preocupaciones que realmente surgieron aparecen en el top-k de resultados. Procedimiento manual, k inicial = 5.
- **Consumo real por un agente** (etapa 2): al menos un reviewer no autoriado del proyecto —humano o agente— completa reviews consultando la memoria de forma regular. Es binario y observable, no una métrica de vanidad.

Estas medidas calibran los umbrales de la etapa 2; esta spec fija el método, no cifras arbitrarias.

## Criterios de aceptación de esta spec

- Cada afirmación de la etapa 1 corresponde a comportamiento verificable en el código, el README o el CI actuales.
- Un lector nuevo entiende en una lectura qué es, qué no es y para quién es, sin abrir el código.
- Los no-goals son accionables: cualquier contribución que comente PRs, puntúe código o pretenda reemplazar al reviewer contradice este documento y debe discutirse aquí antes.
- Las specs hermanas (02-arquitectura, 03-recuperacion, 04-cli, 05-decisiones, 06-roadmap) se referencian por número; este documento no duplica sus detalles.
- Toda métrica propuesta es calculable con datos del propio repositorio y de su memoria, sin cifras de mercado ni citas externas.

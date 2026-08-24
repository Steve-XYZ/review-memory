---
name: review-memory
description: Guided GitHub PR code review that consults the team's historical review memory. Use when asked to review a pull request (or a diff) while the ReviewMemory MCP server with its search and context tools is available; it orders the flow, calibrates every finding against prior team decisions, and reports suppressed findings instead of silently repeating them.
---

# Review guiada por ReviewMemory

Memoria institucional de code review: ReviewMemory recupera discusiones históricas del
equipo con su decisión inferida (`accepted`, `rejected`, `partially_accepted`,
`unknown`). Esta skill ordena el flujo de review para consumirla. La memoria informa;
juzgar el diff sigue siendo tu trabajo.

## Flujo obligatorio

El orden es deliberado y anti-anclaje: primero piensas, después consultas.

1. **Ticket**: entiende qué cambia el PR y por qué.
2. **Inspección del diff**: lee el diff y forma tus hallazgos candidatos SIN consultar
   la memoria todavía. Anótalos todos, aunque sospeches que alguno ya fue discutido.
3. **Consulta de memoria** (antes de cerrar el informe):
   - Obligatorio: `context` del PR en revisión — `context(repo: "owner/name", pr: N)`.
   - Opcional pero recomendado si el diff toca rutas con historia o términos clave:
     `search(query: "<términos del hallazgo>", files: ["ruta/tocada.go"])`.
4. **Validación**: contrasta cada hallazgo candidato contra el código actual Y contra
   el historial devuelto (reglas abajo).
5. **Informe**: findings calibrados + sección de transparencia (abajo).

## Calibración de cada hallazgo

- **Contradice una decisión `rejected`** → omítelo del cuerpo del informe o márcalo
  explícitamente como «ya discutido y descartado», citando `url` y `threadId`.
- **Está respaldado por una discusión `accepted`** → consérvalo y cítalo como
  precedente con su `url`.
- **Coincide solo en parte con una discusión `partially_accepted`** → precedente
  parcial: gana algo de peso, pero exige verificar que el código actual siga aplicando
  la parte que el equipo aceptó antes de apoyarte en ella.
- **`unknown` o sin señal** → peso normal, como en cualquier review.
- La coincidencia se juzga por archivo tocado y por sustancia del hallazgo, no por
  similitud textual superficial. Un score bajo no invalida un precedente pertinente;
  un score alto no lo confirma si el código cambió.

Nota: el contrato JSON actual no expone la procedencia de la decisión (`inferred` vs
`manual`); si una versión futura la añade, úsala para priorizar precedentes manuales.

## Transparencia

Nada se descarta en silencio. Todo hallazgo omitido por historia aparece en una
sección final «Hallazgos descartados por decisiones previas» con: qué se omitió, qué
hilo lo descartó (`url` + `threadId`) y cuál fue la decisión. Si la memoria no aportó
nada relevante dilo también: «la memoria no devolvió precedentes aplicables».

## Degradación — nunca bloquees el review

- `context` responde error `pr_not_indexed` → intenta una sola vez
  `reviewmemory index owner/name --last N` **solo si el binario `reviewmemory` está
  disponible en PATH**; si no lo está, pídele al usuario que indexe el PR. En ambos
  casos reintenta `context` una vez.
- Si el reintento también falla o nadie puede indexar → continúa con lo que `search`
  devuelva más un review normal, declarándolo al inicio del informe:
  «review sin contexto del propio PR: <motivo>».
- BD o servidor MCP no disponibles → continúa como review normal y decláralo al inicio
  del informe: «review sin memoria: <motivo>».
- Un fallo de la memoria nunca aborta ni retrasa el review más ese reintento único.

## Límite de responsabilidad

Una decisión histórica no es prueba de que el código actual sea correcto: valida el
precedente contra el estado actual del archivo. Si el código cambió desde el hilo,
el precedente pierde fuerza y el hallazgo vuelve a peso normal.

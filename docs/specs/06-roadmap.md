# Roadmap posterior a la etapa 1

La etapa 1 ([01-vision](01-vision.md)) sentó las bases: ingesta REST de PRs, búsqueda
FTS + solapamiento + recencia, decisiones inferidas y CLI ([04-cli](04-cli.md)).
Este roadmap ordena lo que viene. El orden es deliberado: primero corregir la calidad
y durabilidad de los datos que ya se indexan, después llevarlos al lugar donde los
reviewers trabajan (agentes vía MCP), y al final las mejoras condicionadas a evidencia.

Cada ítem marca si es **compromiso** (trabajo acordado de esta etapa) o **propuesta**
(se ejecuta cuando su criterio de entrada se cumple). El orden de prioridad:

1. Estado `resolved` vía GraphQL — corrección barata de un dato falso persistido hoy.
2. Re-indexado incremental — evita destruir estado propio en cada corrida de `index`.
3. MCP server — distribución: sin esto, la memoria solo llega a quien tiene el CLI.
4. Integración con skills de review — consumo real del ítem 3 en el flujo de trabajo.
5. Embeddings + pgvector — solo si la medición demuestra que FTS puro se queda corto.
6. Aprendizaje post-review — depende de 1, 2 (datos correctos) y 4 (consumo real).

## 1. Estado resolved vía GraphQL (compromiso)

**Problema.** La API REST de GitHub no expone el estado resuelto de los hilos de
review; `GitHubPullRequestSource` reconstruye los hilos agrupando comentarios por su
cadena de `in_reply_to` y todos llegan con `Resolved = false`
(`src/ReviewMemory.GitHub/GitHubPullRequestSource.cs`). Consecuencia: la columna
`resolved` guarda ruido y [05-decisiones](05-decisiones.md) infere sobre una señal
siempre falsa — un hilo discutido y descartado por el equipo se ve igual que uno
abierto sin respuesta.

**Propuesta.** Consulta GraphQL complementaria tras cargar el PR por REST:
`pullRequest.reviewThreads { id isResolved isOutdated }`, mapeo `thread id → isResolved`
y sobrescritura de `Resolved` antes de persistir. REST sigue siendo la fuente de
comentarios y paginación; GraphQL aporta solo el flag. Si la consulta falla, degradar
al comportamiento actual con aviso en stderr y exit 0.

- Entrada: ninguna; es una corrección de la etapa 1.
- Salida (definition of done): tras `index`, un PR con hilos resueltos conocidos
  persiste `resolved = true` para esos hilos (test contra fixture GraphQL); fallo de
  GraphQL degrada sin romper la indexación.

## 2. Re-indexado incremental (compromiso)

**Problema.** `IndexRepository.UpsertAsync` borra y recrea todos los hilos del PR en
cada pasada (`DeleteThreadsAsync`, `src/ReviewMemory.Storage/IndexRepository.cs`).
Todo lo que viva ligado a esas filas —una decisión corregida manualmente
(`confidence = manual`) o cualquier dato futuro aprendido sobre el hilo— se pierde
cada vez que alguien relanza `index`. Además reescribe filas idénticas: costo
proporcional a todo el histórico en cada corrida.

**Propuesta.** Conciliar hilos entrantes contra existentes por su id estable de
GitHub: insertar nuevos, actualizar los modificados (hash de contenido: finding,
respuestas, path, línea), conservar intactos los que no cambiaron. La re-inferencia
de [05-decisiones](05-decisiones.md) solo aplica a hilos nuevos o modificados; nunca
sobrescribe una decisión manual. Los archivos del PR (`pr_files`, derivables
íntegramente de la API) pueden seguir con replace-all.

- Entrada: ninguna; también es corrección de la etapa 1.
- Salida (definition of done): test de idempotencia — indexar dos veces el mismo PR
  deja el mismo conteo de hilos, comentarios y decisiones; una decisión manual
  sobrevive al re-indexado de un PR sin cambios; un hilo modificado en GitHub sí se
  actualiza.

## 3. MCP server (compromiso)

**Problema.** El único punto de entrada hoy es el CLI ([04-cli](04-cli.md)). Los
consumidores objetivo —Codex, Claude Code, Copilot, Cursor, OpenCode— hablan MCP;
pedirles invocar un binario .NET acopla cada integración a detalles de instalación y
de acceso a Postgres.

**Propuesta.** Nuevo proyecto `ReviewMemory.Mcp` sobre transporte stdio con dos
tools que reutilizan la lógica de Core/Storage sin duplicarla: `search` (query,
repo opcional, files opcional, limit) y `context` (repo, pr, limit), con los mismos
parámetros y el mismo JSON de salida que los comandos homónimos del CLI — el
contrato de [03-recuperacion](03-recuperacion.md) sirve igual para humanos y
agentes. Sin tools de escritura: la memoria se alimenta con `index`, no desde el
agente. Distribución como herramienta .NET autocontenida; instrucciones de registro
por cliente en el README.

- Entrada: ninguna.
- Salida (definition of done): el server registrado en Claude Code u OpenCode
  ejecuta `search` y `context` contra la BD local de docker-compose; paridad de
  salida JSON con el CLI verificada por test; error estructurado si no hay BD, sin
  crash del proceso.

## 4. Integración con skills de code review (compromiso)

**Problema.** Una memoria que nadie consulta no existe. Falta definir en qué momento
del flujo de review un agente pregunta a ReviewMemory y qué hace con la respuesta.

**Propuesta.** Skill que ordena el flujo: ticket → inspección del diff → consulta
`context` del PR (vía MCP, ítem 3) → review → validación de cada finding contra el
código actual **y** contra el historial devuelto. Un finding que contradice una
decisión histórica `rejected` se marca como ya discutido y descartado en vez de
repetirlo; uno respaldado por una discusión `accepted` gana peso.

- Entrada: MCP server disponible (ítem 3).
- Salida (definition of done): un review guiado por la skill cita discusiones
  históricas relevantes con su decisión inferida y omite o degrada hallazgos ya
  descartados por el equipo; la skill queda versionada en este repo.

## 5. Embeddings + pgvector (propuesta)

**Problema.** FTS falla cuando el vocabulario no coincide: "duplicate callback" no
casa léxicamente con "processed twice", aunque describan el mismo problema. El
solapamiento de archivos compensa solo si el PR candidato tocó los mismos ficheros.

**Propuesta.** Segunda señal de similitud: embedding del comentario del reviewer
calculado en la indexación, búsqueda por coseno con pgvector, combinación lineal con
las tres señales de etapa 1. Cuándo vale la pena: no por defecto. Construir primero
un conjunto pequeño de consultas con relevancia conocida sobre repos reales y medir
el recall@k de FTS puro; implementar solo si ese recall queda bajo un umbral
acordado. Si FTS rinde, pgvector es infraestructura y migraciones sin retorno
justificado.

- Entrada: benchmark de recall@k publicado con números que demuestren el déficit de
  FTS puro.
- Salida (definition of done): misma batería antes/después con mejora medida de
  recall@k sin perder precisión en top-1 más allá del umbral acordado; migración
  embebida nueva (`schema_migrations`) y ranking documentado en
  [03-recuperacion](03-recuperacion.md).

## 6. Aprendizaje post-review (propuesta)

**Problema.** La inferencia léxica de [05-decisiones](05-decisiones.md) resuelve lo
evidente y deja `unknown` lo ambiguo; el conocimiento más valioso —qué aceptó o
descartó el equipo y por qué— vive en la conversación posterior y no vuelve a la
memoria.

**Propuesta.** Cerrar el bucle: tras un review que consumió contexto (ítem 4),
registrar la respuesta efectiva ante cada hallazgo consultado (aplicado tal cual,
rechazado con razón, ignorado) y persistirla como decisión manual ligada al hilo
histórico. Ese corpus alimenta "Team Review Patterns": agregados por archivo, módulo
y tipo de hallazgo que `context` devuelve junto a los hilos crudos. Depende de los
ítems 1–2: sin `resolved` real ni re-indexado seguro, lo aprendido se pierde o parte
de señales falsas.

- Entrada: ítems 2 y 3 en producción y uso real del flujo del ítem 4 — sin consumo
  no hay respuestas que aprender.
- Salida (definition of done): una decisión registrada manualmente aparece en
  resultados posteriores de `context` con su procedencia, sobrevive al re-indexado,
  y los patrones agregados aparecen en el JSON de salida documentado.

## No hacer

- LLM reviewer propio: la memoria recupera y puntúa historial; juzgar el diff sigue
  siendo trabajo del agente reviewer (posición de [01-vision](01-vision.md)).
- UI web mientras CLI y MCP cubran el consumo.
- Embeddings por defecto sin la medición del ítem 5.
- Escritura desde agentes consumidores: la memoria la alimentan `index` y el
  aprendizaje post-review supervisado, nunca las tools de lectura.

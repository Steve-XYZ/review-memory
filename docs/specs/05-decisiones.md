# Decisiones

Cada discusión de review termina en un desenlace. En la etapa 1 ese desenlace se **infiere** con señales léxicas deterministas (sin IA) en el momento de indexar: una decisión por hilo, guardada junto a él y expuesta en cada resultado de búsqueda. `confidence` distingue lo inferido de lo que un humano corrija (etapa 2).

Fuente de verdad: `DecisionInferrer.Infer` (`src/ReviewMemory.Core/Decisions/DecisionInferrer.cs`). Si este documento difiere del código, gana el código.

## Desenlaces

| Valor en BD | Significado |
|---|---|
| `accepted` | Finding aceptado — se corrigió la implementación |
| `rejected` | Finding rechazado (won't fix, falso positivo, ya cubierto…) |
| `partially_accepted` | Se atendió en parte; algo queda pendiente |
| `unknown` | Sin señal concluyente o con señales contradictorias |

## Señales

Tres patrones regex compilados, todos `RegexOptions.IgnoreCase`, aplicados como *substring* del cuerpo completo del comentario (sin anclas):

```csharp
// Rechazo
@"won'?t ?fix|wontfix|not an issue|non-?issue|by design|as designed|works as intended|intentional|false positive|already (?:enforced|handled|covered)|no (?:aplica|hace falta)"

// Parcial
@"partially|partial fix|parcialmente"

// Aceptación
@"fixed|addressed|good catch|done in|changed to|refactored|added (?:a )?(?:check|guard|test)|arreglado|corregido"
```

### Rechazo → `rejected`

| Fragmento | Ejemplos que matchea |
|---|---|
| `won'?t ?fix` | "won't fix", "wont fix", "wontfix" |
| `wontfix` | "wontfix" (redundante con el anterior) |
| `not an issue` | "Not an issue: ya hay constraint" |
| `non-?issue` | "non-issue", "nonissue" |
| `by design` | "this is by design" |
| `as designed` | "fails as designed" |
| `works as intended` | "it works as intended" |
| `intentional` | "that's intentional" |
| `false positive` | "False positive from my own tooling" |
| `already (?:enforced\|handled\|covered)` | "already enforced by a unique constraint" |
| `no (?:aplica\|hace falta)` | "no aplica aquí", "no hace falta loggear" |

### Parcial → `partially_accepted`

| Fragmento | Ejemplos que matchea |
|---|---|
| `partially` | "Partially addressed" |
| `partial fix` | "partial fix, rest in follow-up" |
| `parcialmente` | "parcialmente corregido" |

### Aceptación → `accepted`

| Fragmento | Ejemplos que matchea |
|---|---|
| `fixed` | "Fixed, moved the check" |
| `addressed` | "addressed in commit abc" |
| `good catch` | "Good catch, fixed" |
| `done in` | "done in #1234" |
| `changed to` | "changed to TryParse" |
| `refactored` | "refactored into a service" |
| `added (?:a )?(?:check\|guard\|test)` | "added a guard", "added test" |
| `arreglado` | "arreglado en el último commit" |
| `corregido` | "corregido, gracias" |

## Qué comentario cuenta

Candidatos = todas las respuestas del hilo **más** el comentario inicial (finding), ordenados estable así:

1. Primero comentarios de quien **no** escribió el finding — típicamente el autor del PR, que es quien actúa.
2. Después comentarios del propio autor del finding (el reviewer), incluido el finding original.

Por cada categoría (rechazo / parcial / aceptación) gana el **primer** candidato que matchea en ese orden; su autor y cuerpo alimentan la razón. El matching es case-insensitive sobre el body completo: el finding mismo puede portar la señal decisiva (p. ej., un reviewer que marca su propio comentario "False positive … ignore this comment").

## Orden de evaluación

```
rechazo ∧ (aceptación ∨ parcial)  → unknown   razón: "señales contradictorias — {A} / {B}"
rechazo                           → rejected
parcial                           → partially_accepted
aceptación                        → accepted
nada                              → unknown   razón: null
```

- El conflicto exige rechazo frente a otra categoría; **parcial + aceptación sin rechazo cae en parcial** (ver ejemplo 3).
- En conflicto, `{B}` cita la aceptación si existe; si no, la parcial.
- Formato de cada cita: `respuesta de {author}: "{cuerpo}"`, cuerpo recortado a 120 caracteres visibles (119 + `…`) tras `Trim()`.

## Confianza

| Confidence | Origen |
|---|---|
| `inferred` | Inferida por el catálogo anterior (default en BD) |
| `manual` | Reservada para corrección humana (etapa 2): falsos positivos del léxico y desenlaces que el texto no revela |

Hoy ninguna vía del CLI escribe `manual`; toda decisión indexada es `inferred`.

## Restricciones de BD

```sql
CREATE TABLE decisions (
    thread_id  bigint PRIMARY KEY REFERENCES review_threads (id) ON DELETE CASCADE,
    outcome    text   NOT NULL CHECK (outcome IN ('accepted', 'rejected', 'partially_accepted', 'unknown')),
    reason     text,
    confidence text   NOT NULL DEFAULT 'inferred' CHECK (confidence IN ('inferred', 'manual')),
    decided_at timestamptz NOT NULL DEFAULT now()
);
```

(`001_init.sql`; además `idx_decisions_outcome`.) Una decisión por hilo. El indexador persiste por hilo dentro de la misma transacción que el resto del PR: `INSERT … ON CONFLICT (thread_id) DO UPDATE` actualiza `outcome`, `reason`, `confidence` y pone `decided_at = now()`. Como `review_threads` se borra y recrea en cada re-indexado, el `ON DELETE CASCADE` elimina la decisión previa: **re-indexar siempre reinfiere desde cero**, incluso sobre una decisión manual. El contador del CLI ("decisiones con desenlace") excluye los `unknown`.

## Exposición en búsqueda

La búsqueda hace `LEFT JOIN decisions`: un hilo sin fila de decisión aparece como `unknown` sin razón. Cada hit lleva `outcome` y `reason`; la salida de consola imprime `Resolución:` solo si `outcome ≠ unknown` **o** hay razón (el desconocido contradictorio sí se explica). Etiquetas:

| Outcome | Texto |
|---|---|
| `accepted` | Finding aceptado — se corrigió la implementación |
| `rejected` | Finding rechazado |
| `partially_accepted` | Finding parcialmente aceptado |
| `unknown` | Desenlace desconocido |

La razón se añade tras las etiquetas separada por ". ". El JSON serializa `outcome` en camelCase (`"accepted"`). `confidence` existe en BD pero aún no se expone en resultados.

## Ejemplos entrada → salida (de los tests)

| Respuesta decisiva | Outcome | Razón |
|---|---|---|
| "Good catch, fixed by adding an idempotency check." (dev) | `accepted` | — |
| "Not an issue: providerRequestId already enforced by a unique constraint." (dev) | `rejected` | — |
| "Partially addressed; full retry policy lands in a follow-up." (dev) | `partially_accepted` | — |
| dev: "Fixed, moved the check." + reviewer: "Hmm, actually not an issue, the provider dedupes." | `unknown` | "señales contradictorias — respuesta de reviewer: … / respuesta de dev: …" |
| "Sure, will look into it next sprint." (dev) | `unknown` | `null` |
| Finding del reviewer: "False positive from my own tooling, ignore this comment." (+ reply "ok") | `rejected` | — |

## Limitaciones (explícitas)

- Inglés completo, español parcial: solo `no aplica`, `no hace falta`, `parcialmente`, `arreglado`, `corregido`.
- Substring sin límites de palabra ni detección de negación: "not fixed" contiene `fixed` → falso `accepted`; "unfixed" igual.
- No mira diffs posteriores, commits, ni estado del PR; `resolved` del hilo se persiste pero no participa en la inferencia.
- Por categoría se cita solo el primer comentario que matchea, no todos.
- Re-indexar pierde cualquier decisión `manual` (cascade + overwrite); `confidence` no aparece aún en resultados de búsqueda.

## Etapa 2: aprendizaje post-review (propuesta, no implementada)

Combinar señales del ciclo de vida del PR con el léxico actual. Cada señal aporta evidencia hacia un desenlace:

| Señal | Fuerza | Evidencia hacia |
|---|---|---|
| Hilo/comentario marcado `fixed` | Fuerte positiva | `accepted` |
| Respuesta `agreed` del reviewer | Positiva | `accepted` |
| `dismissed` por el reviewer | Negativa | `rejected` |
| Hilo `resolved` | Ambigua | Inspeccionar el diff resultante: ¿cambió el hunk señalado entre base y merge? Cambio ⇒ `accepted`; sin cambio ⇒ queda `unknown` o pasa a revisión manual |
| PR abandonado (cerrado sin merge) | Débil | No concluyente por sí sola; solo refuerza lo que diga el léxico |

Regla de combinación propuesta: la señal más fuerte disponible decide; el léxico etapa 1 sigue siendo el piso para todo PR indexado. Las señales fuertes podrían subir la confianza declarada del resultado sin cambiar `confidence` (que sigue reservado a humanos).

Decisiones manuales:

- Flujo (comando o skill de review) para fijar `outcome` con `confidence='manual'`.
- Una decisión `manual` debe prevalecer sobre cualquier reinferencia y sobrevivir al re-indexado (hoy el cascade la borra): el indexador saltará la inferencia cuando exista fila manual, en vez de borrarla con el hilo.
- Casos de uso: corregir falsos positivos del léxico ("not fixed"), registrar desenlaces que el texto no muestra, y servir como datos de entrenamiento para etapas posteriores.

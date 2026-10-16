# Plan (2026-10-16): anti-bucle, grafo completo y pasada compartida

Estado: **aprobado por el usuario para ejecutar con subagentes** (mensaje del 2026-10-16: «haz una planificación para hacer todo lo que falta y ejecútalo»). Todo sin commitear en `consolidation`. Origen y datos: `docs/experimentos/2026-10-16-pasada-compartida/README.md`, memoria `project_shared_pass_design_2026_10_16`, retomar `docs/project_notes/2026-10-16-bucles-de-herramientas-y-calidad-informes-retomar.md`.

## Objetivo
1. Que un modelo que deja de anotar o entra en un ciclo de herramientas no pueda repetir el incidente de 108 USD.
2. Completar el grafo con los datos exactos que faltan (llamadas, excepciones, APIs sensibles, concurrencia, hash y procedencia) y con nodos para lo que escriba el LLM.
3. Implementar la pasada compartida (lectura por lote del grafo → registro de hallazgos con una severidad), **desactivada por defecto**.

## Reglas comunes (todos los paquetes)
- Ediciones quirúrgicas; leer antes de editar; no tocar ficheros fuera del alcance del paquete. Sin `git commit`/`push`/`stash`/`reset`. No ejecutar análisis reales con LLM ni el CLI con flags no verificados (regla `feedback_never_run_cli_with_unverified_flags`). Tests con LLM falso.
- No añadir opciones a `Framework/` para un sample (`feedback_framework_extension_points`): en Framework solo defectos genéricos.
- No cambiar contratos públicos existentes; lo nuevo es aditivo. Español en comentarios solo si el fichero ya los lleva así; seguir la densidad de comentarios del entorno.
- Criterio de hecho: `dotnet build` de los proyectos tocados sin errores y tests de esos proyectos en verde (línea base: `CodebaseAnalysis.Core.Tests` 1397 verdes el 2026-10-16). Si un test existente cae por el cambio, se arregla el cambio, no el test, salvo que el test fije un comportamiento que este plan cambia explícitamente.
- Los builds comparten `obj/`: si falla por bloqueo de fichero, esperar 20 s y reintentar (hay otros paquetes en paralelo).
- Informe final de cada paquete: ≤ 12 líneas: ficheros creados/modificados, nº de tests nuevos y resultado, desviaciones del plan, dudas abiertas. Nada de diffs.

## Paquetes

### A. Anti-bucle (Framework + sample)  [Sonnet]
1. `Framework/LLM/Steps/BaseLlmStep.LlmInvocation.cs`: sustituir `_lastToolCallKeys`/`_consecutiveIdenticalToolCalls` por una **ventana de las últimas 10 iteraciones**. Una llamada de solo lectura con la misma clave (`LlmHelpers.NormalizeToolCallKey`) en **3 o más iteraciones de la ventana** corta con `StopWithMessage` (mensaje como el actual). Las llamadas que modifican siguen cortando a la 2.ª repetición consecutiva. `ResetToolCallRepeatTracking` limpia la ventana.
2. `Samples/CodebaseAnalysis.Core/Notes/NoteProtocolTool.cs` (+ `NoteSession.cs`, `NoteProtocolOptions.cs`): al devolver un resultado, contar los resultados `[Rn]` **sin anotar de turnos anteriores** (mensajes `tool` crudos anteriores al último mensaje `assistant`). Con ≥ `ReminderFromPending` (1): añadir al final «Pendientes de nota de turnos anteriores: R12-R30 (N)…». Con ≥ `WithholdFromPending` (12): **no devolver el dato**, responder que queda retenido hasta anotar los pendientes. Opciones en `NoteProtocolOptions` (valores por defecto 1 y 12; configuración `CodebaseAnalysis:NoteProtocol`).
3. `Samples/CodebaseAnalysis.Core/appsettings.analysis.json`: `Auditors:MaxToolIterations` y `FlowsAuditor` de 500 a 100. `Chat` no cambia.
4. Tests nuevos en `Samples/CodebaseAnalysis.Core.Tests` (LLM falso): ciclo A,B,A,B y A,B,C,A,B,C cortan; exploración legítima no corta; llamada mutante corta a la 2.ª; recordatorio presente solo con pendientes de turnos anteriores; retención en el umbral; todo desaparece tras anotar.

### B1a. Enriquecedor C#: llamadas exactas, excepciones y constructores  [Opus]
Proyecto `Samples/DotNetEnricher` (+ `DotNetEnricher.Tests`). Hechos medidos en el grafo de `Framework` (copia en scratch; corrida real en `C:\Users\juan.rozano\m\fw\.codebase\codebase.db`): `CALLS` 387 para 431 métodos con `strategy: unique_name, confidence 0.75` (resolución por nombre de cbm; la ejecución de Roslyn tiene `cbm_edges_replaced: 0`); `THROWS` 1 y propiedad `exceptions` en 2 métodos; 36 constructores sin propiedades del compilador (`is_async`, `accessibility`…).
1. **`CallEnricher`**: aristas `CALLS` exactas desde `ReferenceIndex` (invocaciones, `new`, referencias a métodos como delegado, llamadas por interfaz/override resueltas al símbolo declarado), con propiedades `exact:true`, `origin:'compiler'`, `line`, `callee`. Política con cbm: las aristas cbm heurísticas (`strategy: unique_name`) de métodos C# cuyo origen se ha analizado se **reemplazan** por las exactas (registrar `cbm_edges_replaced` en `enrichment_runs`); no tocar aristas de otros lenguajes. Parity test sobre `csharp-simple`: documentar cuántas exactas, cuántas cbm sustituidas y cuántas cbm no tenían equivalente.
2. **`ThrowsEnricher`**: `THROWS` método→tipo de excepción por `throw new X` y `throw;`; propiedades por método `throws` (lista de tipos), `swallows_exceptions` (catch vacío o que solo registra/devuelve, catch genérico), `rethrows`. Sin duplicar `detected_issues` de `IssueEnricher`: reutilizar sus reglas si existen.
3. **Constructores y propiedades con lógica**: los enriquecedores que hoy dejan sin tocar los constructores (nodos `Method` con el nombre de la clase) deben patchearlos igual (rango, `accessibility`, `cyclomatic`, `param_types`, `signature`, `contract`). Investigar por qué fallan (`unresolved` de `enrichment_runs`) y arreglar la causa.
Todo registrado en `EnrichmentPipeline` con su `Order`. Tests en `DotNetEnricher.Tests` (patrón de `EnricherTests`/`TestGraph`).

### B1b. Enriquecedor C#: APIs sensibles, concurrencia, hashes y procedencia  [Opus]
(Después de B1a; mismo proyecto.)
1. **`SensitiveApiEnricher`**: propiedad por método `sensitive_apis` (lista de `{category, api, line}`) y `sensitive_categories`. Categorías: `file-io` (File.*, Directory.*, FileStream, StreamReader/Writer sobre rutas), `process`, `network` (HttpClient, Socket, WebClient), `deserialization` (BinaryFormatter, JsonConvert con TypeNameHandling, YamlDotNet deserialize), `reflection` (Activator, Type.GetType, Assembly.Load, MethodInfo.Invoke), `dynamic-code` (compilación/Expression), `environment` (Environment.GetEnvironmentVariable), `sql`. Resolución por símbolo, no por texto.
2. **`ConcurrencyEnricher`**: por método `concurrency_markers` (`lock`, `Interlocked`, `async-void`, `fire-and-forget` (Task no esperada), `blocking-wait` (.Result/.Wait()/GetAwaiter().GetResult()), `Task.Run`, `Parallel.*`, `writes-static-mutable`); por campo `is_static_mutable`, `is_thread_safe_type`.
3. **Hashes**: por `Method`, `body_hash` (sha256 del cuerpo sin trivia) y `contract_hash` (firma completa + accesibilidad + `throws`); por tipo, `type_hash` (hashes de sus miembros ordenados). Deterministas entre ejecuciones.
4. **Procedencia uniforme**: todo nodo y arista patcheado/creado lleva `enriched_by` (lista de enriquecedores), `enriched_at`, `enricher_version`, y las aristas `origin`. Hoy `enriched_by` solo está en clases/interfaces/campos (0 de 431 métodos). Extender `EnrichmentMarks` sin romper los consumidores existentes.
Tests en `DotNetEnricher.Tests`; actualizar el README del enriquecedor con las propiedades nuevas.

### B2. Enriquecedor Java  [no se ejecuta]
`Samples/CodebaseAnalysis.Core/Enrichment/Java/JavaAnalyzer.java` requiere javac 17 (*source launcher*); la máquina solo tiene un JRE 1.8 y el JDK de las pruebas estaba en el scratch de una sesión anterior. **No se escribe código Java sin poder compilarlo.** Entregable: sección en este plan con la paridad exigida (los mismos nombres de propiedad que B1a/B1b) para cuando haya JDK.

### C. Nodos para lo que escribe el LLM en el grafo  [Sonnet]
`Samples/CodebaseAnalysis.Core/Enrichment/AnalysisGraph/` (patrón de `InvestigationNoteGraphWriter`, `AnalysisGraphStore`, `CodeNodeLocator`) y vistas de `Tools/GraphDomainAnalysisViews.cs`.
- Etiquetas nuevas: `Finding` (id `REG-nnn`, kind bug|debt|security, category, title, rationale, severity P0-P3, severityBasis, status, confidence, reverified), `Anchor` (path, start_line, end_line, quote, validated), `Summary` (level symbol|file|subsystem|system, text), `Batch`, `Run` (runId, startedAt, models, promptVersions).
- Aristas: `Finding -AT-> Anchor`, `Anchor -IN-> File`, `Finding -ABOUT-> símbolo`, `Finding -FROM_SEED-> Issue/símbolo`, `Finding -INSTANCE_OF-> Finding`, `Summary -SUMMARIZES-> nodo`, `nodo -IN_BATCH-> Batch`.
- Contratos LLM como propiedades del método con prefijo `llm_` (`llm_does`, `llm_inputs`, `llm_outputs`, `llm_side_effects`, `llm_throws`) y `origin='llm'`, `llm_run`, `llm_input_hash`.
- Procedencia en todo lo escrito: `origin`, `producer`, `run_id`, `file`, `start_line`, `end_line`, `input_hash`.
- Escritores idempotentes por `Run` (reescribir un Run sustituye el anterior; los nodos `stale` se marcan, no se borran en silencio).
- Vista `graph_domain` nueva `findings` (filtro por severidad/kind/símbolo) y `contracts` (por símbolo). Las consultas por defecto excluyen `origin='llm'` salvo en esas vistas.
- Fixtures: `docs/experimentos/2026-10-16-pasada-compartida/registry.json` y `out/*.json` (formato real de los datos).
Tests en `CodebaseAnalysis.Core.Tests` con una BD SQLite temporal.

### D. Pasada compartida  [Opus]  — desactivada por defecto
Carpeta nueva `Samples/CodebaseAnalysis.Core/SharedPass/` + plantillas en `templates/` + configuración `CodebaseAnalysis:SharedPass` (`Enabled=false`, `MaxBatchTokens=50000`, rol/modelo, topes de coste por lote/etapa/ejecución con corte duro). Datos y prompts de partida: `docs/experimentos/2026-10-16-pasada-compartida/{instructions_E3.md,instructions_E4.md,scripts/build_packets.py,validate.py}`.
- **D1 Lotes y paquetes (determinista)**: un lote por subsistema del mapa de dominios; si supera el presupuesto, se parte con `CommunityDetector` a mayor resolución. Paquete = código numerado + hechos del grafo (rangos, métricas, issues, código muerto, herencia, DI, y las propiedades nuevas de B1) + relaciones con otros subsistemas. Orden de lotes por niveles (compartido primero).
- **D2 Lectura por lote (LLM, sin herramientas, sin conversación)**: una llamada por lote; salida JSON con esquema validado; validación en cascada (esquema → cobertura de ficheros y símbolos públicos → ancla literal en `[start-3, end+3]`, con reubicación y marca `relocated` → `symbolId` existente); reintento solo de lo que falla (máx. 2) y después partir el lote; lo irrecuperable queda como `coverageGap`. El modelo no devuelve severidad.
- **D3 Consolidación y severidad**: deduplicación determinista (símbolo + categoría + solape de anclas, ids estables), severidad por rúbrica (`impact × likelihood × exposure`, exposición desde el grafo) con tope de P2 si la activación exige configuración no por defecto o uso erróneo; un LLM revisa por tandas comparativas los candidatos P0/P1, la seguridad y los de confianza baja, con el fragmento de código real ±N líneas entregado por código (sin agente). Escribe el registro en el grafo (paquete C) y `registry.json` en la carpeta de informes.
- Cableado opcional en `AnalysisPipelineFactory` como etapa previa cuando `Enabled=true`; los informes **no cambian** en esta tanda (consumirlos es la fase siguiente, con ejecución real y OK del usuario).
Tests con LLM falso sobre los fixtures del experimento; ningún test llama a un LLM real.

### R. Revisión independiente  [code-reviewer, solo lectura]
Tras A, B1a, B1b, C y D: revisión de los diffs de los paquetes (correctitud, regresiones, contratos, concurrencia, tests que no prueban lo que dicen). Hallazgos P0/P1 vuelven al paquete correspondiente.

## Orden
Ronda 1: A ∥ B1a. Ronda 2: B1b ∥ C. Ronda 3: D (D1 → D2 → D3). Ronda 4: R + verificación global (build de la solución, todos los tests de `CodebaseAnalysis.Core.Tests`, `DotNetEnricher.Tests`, `OpenAILLmService.Tests`). Un único informe final al usuario.

## Fuera de alcance (siguiente fase, con OK del usuario)
Que los informes consuman el registro y los contratos; prueba real con GPT-SOL con tope; recall en `csharp-simple`; A/B carpeta-prosa frente a lote estructurado; enriquecedor Java con JDK 17; incrementalidad por hash.

## Estado de ejecución (2026-10-16, noche)
A, B1a, B1b, C y D1-D3 hechos y revisados por un revisor independiente; correcciones aplicadas (F1 anti-bucle y notas, F2 enriquecedor C#, F3 pasada compartida). B2 (Java) no se ejecutó: solo hay JRE 1.8, no un JDK 17; la paridad exigida son los mismos nombres de propiedad que B1a/B1b (`throws`, `swallows_exceptions`, `sensitive_apis`, `sensitive_categories`, `concurrency_markers`, `body_hash`, `contract_hash`, `type_hash`, CALLS con `exact:true`, procedencia `enriched_by`/`enriched_at`/`enricher_version`). Verificación: Core.Tests 1491, DotNetEnricher.Tests 81, OpenAILLmService.Tests 118, web compila. Sin commit. Desviaciones y dudas en el informe final de la sesión; rechazado P2-12 (INSTANCE_OF necesita partir los patrones en entradas y cambia el contrato del registro).

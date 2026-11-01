# Capa de conocimiento persistente: análisis y spec (2026-11-01)

Origen: decisión del usuario (memoria `project_knowledge_layer_plan_2026_11_01`). Tres análisis de solo lectura con subagentes; las cifras y rutas de aquí salen de sus informes, con `[V]` leído, `[I]` inferido y `[?]` no sabido.

## 1. Hallazgos que cambian el plan

1. **Las notas `<nota>` no son la fuente del conocimiento.** Las usan los pasos de informe (investigación, auditores). La pasada compartida (`SharedPass/`) es una llamada única sin herramientas ni conversación, y no usa notas `[V]` (`shared_pass_reader_system.md:7`). Como este run no genera informes, las notas no intervienen. Lo que ya produce la lectura es `purpose` por tipo, `does/inputs/outputs/sideEffects/throws` por miembro y resumen por fichero, con anclas validadas `[V]` (`shared_pass_reader_system.md:33-43`, `ReadingValidator.cs:106-109`). **Decisión:** el conocimiento persistente sale de la lectura (se amplía su esquema). La persistencia write-through de las notas se aplaza a la fase de informes.
2. **Hoy un corte pierde el trabajo.** La lectura va a memoria y se persiste al terminar `[I]` (`SharedPassStep.cs:167`). El usuario exige fases independientes y guardado incremental. Existe `InputHash` por lote `[V]` (`BatchReader.cs:474,788`): es la clave de reanudación.
3. **No hay modo «sin informes».** `RequestedReports` vacío significa «todos» `[V]` (`AnalysisPipelineFactory.cs:177-179`). Hay que añadir un flag.
4. **La copia `m\view-delta` no trae código `[V]`** (solo `.codebase/` y `reports/`). El original existe `[V]`: `C:\BBVA\Fuentes\cli-bbva-clan\TEDT\Construccion\delta` (con `Batch/ Online/ Parent/ SpringBatch/`). Se lanza con `--repo` al original y la BD y la salida de la copia.
5. **Coste.** Sobre una sola muestra (fw2, 14 kLOC C#), `gpt-6-sol` extrapola a 350-700 USD para 3,74 M líneas `[I]`; el tope de 150 USD cubre ~20-40 %. `gpt-6-luna` sale a ~30 USD y las nano a ~27-35, con calidad sin medir `[?]`. Los lotes se leen de mayor a menor `[V]` (`BatchReader.cs:489`), no por relevancia.
6. **Biblioteca.** No existe «solo lectura» `[V]`; ni `custom` ni `document` sirven (editables o con efectos en los auditores). Hace falta un `SourceKind` nuevo.
7. **Hash.** Si la wiki, los embeddings y el grafo cuelgan del texto del LLM, cada análisis lo invalida todo. Se enganchan a un hash de las entradas (contenido de ficheros + `InputHash` de lotes).

## 2. Decisiones de diseño

- **D1.** Fuente de verdad: md + json por unidad (subsistema) en `.codebase/units/<unitId>/unit.md` y `unit.json`. El grafo, los embeddings y la wiki son derivados.
- **D2.** La unidad se genera **sin LLM adicional**: se renderiza de lo que devolvió el lector (validado), fusionando los lotes `-pN` del mismo subsistema. El único LLM extra es nombrar dominios (barato, con validación en código).
- **D3.** El lector gana un array `knowledge[]`: `{kind: rule|data|glossary|cut, title, text, evidence: V|I, anchors[{path,startLine,endLine,quote}]}`. `rule/data/glossary` exigen ancla con cita literal (validada por programa). `cut` (corte de modernización) es recomendación: `evidence: I`, sin ancla obligatoria.
- **D4.** Reanudación: cada lote persiste su resultado en `.codebase/units/_batches/<batchId>.json` con `inputHash`, modelo, coste y estado. Al relanzar, un lote con el mismo `inputHash` y modelo se salta.
- **D5.** Orden de lectura por prioridad (subsistemas con endpoints que escriben en tablas primero), filtro opcional por subsistemas y tope de coste duro: 150 USD en total. La pasada se lanza primero como **piloto** (2 subsistemas) para comparar `luna` y `sol` por tasa de anclas válidas.
- **D6.** Biblioteca: `SourceKind.Unit`, clave `unit:<id>`, solo lectura (sin Eliminar ni Versiones ni `update_report`), no entra en la faceta de auditores. Los de los informes del catálogo se retiran del grafo antes de auditar (`DetectAndIndexGraphStep.cs:142-168`): los documentos de unidad **no** deben alimentar a los auditores (circularidad).
- **D7.** Grafo: nodos `UserDocument`/`UserDocSection` reutilizados (evitan tocar 4 listas de exclusión) con aristas `ABOUT` exactas al `Subsystem` (el productor lo conoce, sin heurística). Se reescriben al final de cada análisis (un `ForceReindex` los borra).
- **D8.** Índice semántico: paso por lotes tras generar las unidades (`text-embedding-3-small`, trozos de 1.800 con solape 200; coste en céntimos `[I]`).
- **D9.** Wiki: las páginas de dominio salen de las unidades; dominios de negocio por agrupación de subsistemas por tablas compartidas (grafo bipartito subsistema-tabla, descartando tablas «hub»), con el LLM solo para nombrar. Hash de entradas, no de prosa. Se hace después de A, B y C.
- **D10.** Fuera de alcance: persistir las notas `<nota>`, informes, y aplicar la modernización (vault).

## 3. Paquetes de trabajo

| WP | Contenido | Propietario de ficheros |
|---|---|---|
| A | Lector `knowledge[]`, validación, persistencia por lote y reanudación, prioridad y filtro, flag sin informes, generación de unidades, config de roles/topes | `Samples/CodebaseAnalysis.Core/SharedPass/`, `AnalysisPipelineFactory.cs`, `Models/`, `Hosting/`, plantillas del lector, config, tests |
| B | `SourceKind.Unit`, `UnitStore`, API de unidades, herramientas y prompt del chat, `ClaimAuditor`, `semantic_search`, publicación (embeddings por lotes + nodos del grafo) | `Samples/CodebaseAnalysis.Core/Knowledge/`, `Chat/`, `Tools/`, `Samples/CodebaseAnalysisWeb/Api|Knowledge|Chat/` |
| C | UI: biblioteca plegable con chevron en el borde, pestañas Chat/Visor en el centro, Resumen con líneas de código y pestañas, categoría «Unidades» solo lectura | `Samples/CodebaseAnalysisWeb/ClientApp/`, `Api/GraphDtos.cs`, `Graph/GraphService.Summary.cs`, `Api/GraphEndpoints.cs` |
| D | Wiki desde unidades + dominios por tablas | después |

## 4. Contrato entre paquetes

- `unit.json`: `{schemaVersion:1, unitId, title, subsystemId, generatedAt, inputHash, model, batches[], files[{path,lines}], purpose, members[{qn,path,startLine,endLine,does,inputs,outputs,sideEffects,throws}], knowledge[...D3], tables[{name,access:"read"|"write"}], openQuestions[], coverage:{filesRead,filesTotal}}`.
- `unit.md`: render legible de `unit.json`, con encabezado de procedencia («generado por el análisis, solo lectura») y las anclas como `ruta:líneas`.
- API (B): `GET /api/units` → `[{id,title,subsystemId,inputHash,generatedAt,coverage,knowledge:{graph,embeddings}}]`; `GET /api/units/{id}` → `{summary, markdown}`. Sin POST/PUT/DELETE.
- Clave de fuente: `unit:<id>`; id con el mismo saneado que el slug de la wiki.

## 5. Restricciones para todos los agentes

Sin `git commit`/`push`. Sin ejecutar análisis ni llamadas a LLM reales. Compilar con `-p:OutDir=C:/Users/juan.rozano/m/<carpeta>/` (el back de 5180 bloquea las DLL). Tests de lo tocado. No tocar ficheros de otro paquete. Hay 123 ficheros sin commitear que no son tuyos: no revertir nada. Informe final breve con lo hecho, lo no verificado y los ficheros tocados.

## 6. Verificación de contrato A↔B↔C (2026-11-01)

Test de integración `Samples/CodebaseAnalysis.Core.Tests/KnowledgeUnitsIntegrationTests.cs`: lotes guardados con `BatchResultStore` → `KnowledgeUnitsStep.RunAsync` (A) → `UnitStore` (B) → `UnitPublisher` sin embeddings sobre un grafo mínimo → `KnowledgeGraphIngestor.GetStateAsync`.

- **Coinciden sin tocar nada** `[V, test]`: `batches[]` (A escribe objetos `{id,inputHash,status,model}`, B lee `id` y también acepta strings), `tables[{name,access}]`, `coverage`, `files[{path,lines}]`, `members[].qn`, `knowledge[]` con `anchors[{path,startLine,endLine,quote}]` y `evidence` V/I, `openQuestions` como objetos (B los aplana a `símbolo: pregunta (necesita: a; b)`), `generatedAt` UTC, `inputHash` (el que B guarda como SHA de la fuente, así el estado de grafo sale `present`).
- **Carpeta**: `KnowledgeUnitPaths.Resolve` (A) y `KnowledgePaths.UnitsPath` (B) dan la misma ruta cuando la BD es `<workspace>/.codebase/codebase.db`, incluido el piloto con `--repo` al original y la BD en la copia (el workspace de la web es la copia). Si la BD no está en una carpeta `.codebase`, A cae a `<repo>/.codebase/units`, que es lo que mira B solo si el workspace es ese repo.
- **Defecto arreglado (A)**: `KnowledgeUnitsStep.UnitIdOf` generaba ids de hasta 87 caracteres cuando dos subsistemas con nombre largo colisionaban (slug de 80 + `-huella`); `WikiPaths.IsValidSlug` (B) admite 80, así que `UnitStore.List` descartaba esas unidades sin avisar. Ahora el slug base se recorta para que quepa la huella.
- **Extensiones de A que B ignora** (campos de más, sin consecuencia): `contentHash`, `files[].role|summary`, `types[]`, `members[].type`.
- **C**: `UnitSummaryDto` de `types.ts` se alineó con la forma real de `GET /api/units` (`model`, `counts{members,tables,knowledge,estimates}`, `knowledge: KnowledgeState`, `coverage` y `generatedAt` nullable).

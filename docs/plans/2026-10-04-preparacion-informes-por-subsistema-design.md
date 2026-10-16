# Spec: preparación de datos para los auditores e investigación por subsistema

Estado: **borrador pendiente de aprobación (gate)**, 2026-10-04. Nada de esto está implementado. `[V]` leído en código o en la
base de datos con su `fichero:línea`; `[I]` inferido; `[?]` sin comprobar. Valores por defecto de K (8), concurrencia (4) y
modos A/B **aceptados por el usuario** el 2026-10-04; los coeficientes de tiempo siguen siendo calibrables.

## 1. Problema

Los informes que genera la app sobre el repositorio Java real son muy básicos (observación del usuario, 2026-10-04). Un análisis
externo (GPT Sol) lo atribuye a que cada auditor recibe un universo grande de candidatos y pasa de explorar a redactar sin ninguna
puerta que impida generalizar una muestra local bien citada a una conclusión global incompleta. Su ejemplo, que **el usuario ha
comprobado y confirma como real**, es que el 01 funcional reconstruyó cuatro casos de uso sin reconocer Notificaciones como
capacidad propia, con reglas, estados, eventos y flujos que su documento funcional sí recoge.

Ese caso tiene una particularidad que condiciona el diseño `[V]`: en el índice de cbm de `Online`
(`~/.cache/codebase-memory-mcp/C-BBVA-…-Online-src.db`) hay 258 clases con «notific» en su ruta, repartidas al menos entre
`geo/struts/supercontrato/entidadesParticipantes/oficinas/notificacionOficinas/` y
`geo/struts/supercontrato/entidadesParticipantes/actions/multiRol/notificaciones/`. La capacidad es transversal al árbol de
paquetes, así que agrupar solo por prefijo de paquete la partiría en varios fragmentos que ninguno de los nueve auditores
reconocería como una unidad (§4.2).

Los informes de los repos de evaluación (24-25 ficheros) tienen 6-20 KB `[V]`
(`eval-repos/results/2026-10-01-verifier/csharp-simple/seeded/after/`); eso no dice nada del repositorio grande, donde el problema
aparece.

La hipótesis de trabajo `[I]` es que los auditores resumen las facetas agregadas y el grafo en lugar de recorrer el repositorio por
sus partes funcionales, y que un único contexto con un límite de iteraciones de tools no alcanza para cubrir un repo grande.

## 2. Alcance

Dentro: tres pasos deterministas de preparación (inventario, descubrimiento de dominios, plan de cobertura), un paso de
presupuesto de tiempo dinámico, un modo de investigación por subsistema para los informes de dominio (01, 04, 05, 06) y un
protocolo con briefing para el resto (02, 03, 07, 08, 09).

Fuera, por decisión del usuario: los nueve documentos y su contrato no cambian; el verificador sigue siendo a demanda; no se
fusionan informes; no se introducen expedientes de evidencia en JSON, crítico adversarial ni verificador que reabra la
investigación (propuestos por GPT Sol, aplazados hasta medir esta primera tanda). La genericidad es una restricción: nada del
repositorio de prueba (nombres de subsistemas incluidos) puede aparecer en prompts, heurísticas ni configuración.

## 3. Pipeline propuesto

Los pasos 1-4 y 10-11 existen hoy `[V]` (`AnalysisPipelineFactory.cs:113-151`); los marcados con ★ son nuevos.

1. `DetectAndIndexGraphStep` (cbm).
2. `LanguageEnricherStep`: analizador Java y plugin Struts/JSP. Detecta los lenguajes y solo lanza Java si hay `.java`
   (`LanguageEnricherStep.cs:77,81`).
3. `DotNetEnricherStep`: Roslyn para C#. Se salta solo si hay Java y no hay C# (`DotNetEnricherStep.cs:52-61`); con un
   repositorio sin ninguno de los dos pasa al fallback del LLM.
4. `SqlFacetExtractorStep` (facetas actuales).
5. ★ `RepositoryInventoryStep`.
6. ★ `DomainDiscoveryStep`.
7. ★ `CoveragePlanningStep`.
8. ★ `TimeBudgetStep`.
9. Informes en paralelo (`TolerantReportsParallelStep`): 01, 04, 05, 06 en modo B; 02, 03, 07, 08, 09 en modo A.
10. `ExecutiveSynthesizerStep`, que además recibe la cobertura de cada informe.
11. Verificador a demanda, sin cambios.

Los guards por lenguaje de los pasos 2 y 3 se mantienen tal cual. `LanguageDetectionStep` no existe en el código `[V]` (solo
`SwitchStep` en el framework), y no hace falta crearlo para esta tanda: el inventario lee `DetectedLanguages` del contexto
(`LanguageEnricherStep.cs:77`) y declara qué lenguajes quedaron sin enriquecer.

## 4. Pasos nuevos

### 4.1 `RepositoryInventoryStep` (sin LLM)

Entrada: el grafo, el escaneo de ficheros y la tabla `enrichment_runs`. Salida: `reports/_repository_inventory.json` y la clave de
contexto correspondiente. Contenido:

- **Módulos:** derivados de los ficheros de build presentes (`pom.xml`, `build.gradle`, `.csproj`, etc.); sin ninguno, un único
  módulo raíz.
- **Source sets:** producción, test, qa, debug, generado, samples y `unknown`, clasificados por convención de ruta. Cuando no hay
  evidencia se declara `unknown` y no se adivina.
- **Entradas:** acciones Struts, `main`, procesos batch y planificados, consumidores de mensajes, según lo que el grafo y los
  plugins hayan dejado.
- **Persistencia y configuración:** tablas referenciadas y ficheros de configuración o recursos.
- **Estado por lenguaje:** enriquecido (con la pasada registrada), sin enriquecer o fallido. Un lenguaje sin enriquecedor (JS/TS, por
  ejemplo) se declara como tal y no genera aristas inventadas.
- **Cobertura del grafo:** ficheros en disco frente a nodos en el grafo. El analizador Java ya mide la deriva de cbm (ficheros no
  indexados, también por MAX_PATH, y ratios de nombres que no casan) y la guarda en `cbm_match`
  (`JavaGraphWriter.cs:812-829`); el inventario la reutiliza cuando existe y cuenta por su cuenta cuando no (repositorios sin Java).
- **LOC de producción:** líneas físicas de los ficheros de producción, por lenguaje y por fichero, contadas en el propio paso. No
  se puede apoyar en el grafo `[V]`: en el índice de `Online` los nodos `Class` llevan `"lines":0` en sus propiedades, y la
  consulta de facetas solo usa `loc_total` o, a falta de él, el tramo de líneas del nodo (`SqlFacetExtractorStep.cs:760`).
- **Robustez de lectura:** las consultas sobre `properties` usan `json_valid`, porque en esa misma base hay 60 nodos con JSON
  inválido (55 `Class`, 4 `Function`, 1 `Method`) y `json_extract` lanza `malformed JSON` sobre ellos.

Determinista: dos ejecuciones sobre el mismo grafo y el mismo disco dan el mismo JSON, salvo la marca de tiempo.

### 4.2 `DomainDiscoveryStep` (determinista + LLM que nombra)

Los candidatos a subsistema se forman por tres señales del propio repositorio, combinadas porque ninguna basta sola:

1. **Estructura:** prefijo de paquete o carpeta (profundidad adaptativa), acciones y JSP de Struts del mismo path y tablas que
   tocan los mismos DAO.
2. **Grafo:** una detección de comunidades propia, calculada en este paso. No se puede esperar la de cbm `[V]`: las facetas leen
   `community_id` (`SqlFacetExtractorStep.cs:450-457`), pero ningún nodo del índice de `Online` lo trae (0 de 148 356 métodos,
   7 260 clases y el resto). Se hace sobre el grafo agregado a nivel de tipo (clases e interfaces de producción; unos 8 000 nodos en
   `Online` frente a 148 000 métodos), con las aristas `CALLS`, `USES_TYPE`, `READS`/`WRITES`, `INHERITS` e `IMPLEMENTS` que existan.
   Debe ser determinista: semilla fija, nodos y aristas ordenados, mismo resultado en dos ejecuciones (A2).
3. **Vocabulario:** los tokens de los nombres de paquete, clase y tabla (partidos por mayúsculas y separadores, sin stopwords
   genéricas como `impl`, `dao`, `action`, `util`) permiten fundir comunidades que comparten un término dominante aunque cuelguen de
   ramas distintas del árbol. Es la señal que reúne los fragmentos de Notificaciones. Los tokens se calculan por repositorio;
   ningún término de dominio se escribe en el código ni en la configuración (A9).

Cuando el grafo es pobre (repositorio sin referencias resueltas) manda la estructura, y el inventario lo declara. Cada cluster
recibe un identificador estable derivado de su contenido (carpeta raíz o hash de sus ficheros), de modo que dos ejecuciones sean
comparables.

El LLM recibe un resumen compacto de cada cluster (tamaño, entradas, entidades, tablas) y devuelve nombre, descripción corta y
prioridad. Su salida se valida en código: todo cluster tiene que quedar asignado, no puede descartar ni inventar subsistemas, y lo
que no responda coherentemente conserva un nombre derivado del paquete. Cada fichero de producción queda asignado a un único
subsistema o a `unassigned`. Salida: `reports/_domain_map.json`.

### 4.3 `CoveragePlanningStep` (determinista)

Puntúa cada subsistema con pesos configurables: centralidad en el grafo, número y variedad de entradas, tablas distintas,
estados o enums propios, integraciones externas y riesgo de uso dinámico. Los subsistemas de test, qa y código generado se
excluyen. Para cada informe en modo B fija:

- los K subsistemas elegidos por puntuación, más una «cola larga» muestreada de los que quedan fuera;
- las preguntas de la investigación, que salen de una plantilla por tipo de informe (y no del LLM) y son genéricas;
- la evidencia mínima exigida por subsistema (entradas, flujos completos, caminos alternativos, al menos un contraejemplo);
- los subsistemas excluidos y el motivo, para que el apartado «Cobertura» del informe sea honesto.

Salida: `reports/_coverage_plan.json`.

### 4.4 `TimeBudgetStep` (determinista)

El tiempo de cada pieza deja de ser una constante y pasa a depender del tamaño del código. Los pasos de auditor y sus envoltorios
de modo B los crea la factoría, que construye un `AuditorStepRegistry` por pipeline (una instancia, sin estáticos) y lo entrega
por constructor al `TimeBudgetStep`. El paso calcula los tiempos y los **escribe directamente en la propiedad `Timeout` de cada
paso registrado**:

- `[V]` es viable sin tocar el framework: `StepBase.Timeout` es `virtual { get; set; }` (`StepBase.cs:69`), `TimeoutMiddleware`
  lo lee al ejecutar (`TimeoutMiddleware.cs:30`), y los auditores heredan de `StepBase` a través de `BaseLlmStep`
  (`BaseLlmStep.cs:45`) y se construyen ya asignando `Timeout` (`AnalysisPipelineFactory.cs:388`).
- `[V]` los pasos que implementan `IStep` a mano tienen `init` (`DotNetEnricherStep.cs:43`, `VerifyReportStep.cs:49`). El
  envoltorio de modo B es de este tipo, así que declara `Timeout { get; set; }`; no copia el `init` del verificador.
- El envoltorio de modo B lleva además una tabla `subsistema → TimeSpan` que `TimeBudgetStep` rellena y que él aplica con un
  token propio a cada investigación interna, como hace el verificador con la suya (`VerifyReportStep.cs:75-83`).
- La factoría construye el pipeline en cada ejecución `[V]` (`AnalysisRunner.cs:55`, `:88` y `:121` llaman a
  `CreatePipeline`, `CreateRegenerationPipeline` y `CreateVerificationPipeline`, y `CreateReportStep` crea instancias nuevas,
  `AnalysisPipelineFactory.cs:369-392`), de modo que escribir `Timeout` no se comparte entre ejecuciones.

Fórmulas, con coeficientes en configuración (`CodebaseAnalysis:TimeBudget`) y valores por defecto **provisionales** hasta medir:

- investigación de un subsistema: `clamp(base + k · LOC(subsistema), mín, máx)`;
- consolidación del informe: `clamp(base_c + k_c · (nº de subsistemas elegidos), mín_c, máx_c)`;
- auditor en modo A: `clamp(base_a + k_a · LOC(producción total), mín_a, máx_a)`.

Conciliación con el techo global: se mantiene el límite del pipeline (`CodebaseAnalysis:Timeouts`, 240 min por defecto según las
notas; 0 = sin límite). Si la suma de presupuestos, dividida por la concurrencia efectiva, lo superase, el paso reduce K en los
informes empezando por los subsistemas de menor puntuación y registra un aviso con el recorte; nunca deja que el pipeline muera
por el techo global sin haberlo advertido. Salida: `reports/_time_budget.json`, con las entradas, los coeficientes y los
tiempos asignados.

## 5. Los informes

### 5.1 Modo B: 01 funcional, 04 flujos, 05 errores, 06 arquitectura

Por fuera es el mismo paso por documento con el mismo contrato (un `.md`, mismo nombre, mismo `analysis-meta`). Por dentro es
un `SubsystemInvestigationReportStep` que sigue el patrón de `VerifyReportStep` `[V]`: implementa `IStep` y llama directamente
a sus pasos LLM interiores con `ExecuteStepAsync` y un token propio (`VerifyReportStep.cs:83`), sin pasar por el middleware.

1. **Investigaciones.** Por cada subsistema elegido en el plan lanza un `CustomStatelessTemplateLlmStep` con salida
   `LlmStringStepResult` (el tipo que ya usan el enriquecedor, el verificador y la síntesis, `AnalysisPipelineFactory.cs:120,260,333`),
   con el mismo system prompt y las mismas tools que el auditor actual, y una plantilla nueva por informe
   (`report_<tipo>_investigacion_prompt.md`). Su entrada (`SubsystemInvestigationInput`) lleva los miembros del subsistema, sus
   entradas, las preguntas y la evidencia mínima. Corren en paralelo con el tope configurable.
2. **Notas.** Cada investigación devuelve **texto plano con citas** `ruta:línea`, sin JSON ni etiquetas (decisión vigente), y no
   escribe ningún fichero.
3. **Consolidación.** Se reutiliza el paso de auditor existente, que es el único punto de escritura del informe `[V]`
   (`CustomStatelessTemplateLlmStep.cs`, bloque `SingleReportResult`: nombre de fichero, `analysis-meta`, procedencia,
   `report.written`). Solo cambia su entrada: `ReportFacetInput` (una clase plana de propiedades de texto, una por
   placeholder, `Models/ReportFacetInput.cs`) gana `InvestigationNotes` y `CoverageSummary`, y su plantilla incorpora el apartado
   **Cobertura** (subsistemas cubiertos, excluidos con motivo y fallidos). Así el contrato del documento no se toca.
4. **Fallos.** Si una investigación falla o agota su tiempo se emite un aviso y ese subsistema queda en «no cubierto»; el informe
   se escribe igual. Si fallan todas, se mantiene el error del paso (como hace `TolerantReportsParallelStep` cuando fallan todas
   las ramas, `TolerantReportsParallelStep.cs:34-38`). No se falla duro mientras quede evidencia (regla vigente).
5. **Reintentos y tope de herramientas.** Cada investigación hereda `MaxToolIterations` y `ToolLimitFinalAnswerPrompt` del
   auditor, de modo que agotar el tope degrade a redactar con lo consultado en vez de fallar
   (`CustomStatelessTemplateLlmStep.cs`, `CanAnswerWithoutTools`).

### 5.2 Modo A: 02 smells, 03 muerto, 07 APIs, 08 deuda, 09 DI

El auditor sigue siendo único y recibe, como bloque de contexto, el inventario, el mapa de dominios y el plan, con un protocolo de
mínimos en el prompt y el mismo apartado «Cobertura». Su `Timeout` lo fija `TimeBudgetStep` según el tamaño total de producción.

### 5.3 Validación y síntesis

`ReportValidator` (ya existe) añade la comprobación de que los informes en modo B tengan el apartado «Cobertura» y que cada
subsistema elegido en el plan aparezca en él. La síntesis ejecutiva incorpora la cobertura de cada informe para no presentar como
completo un análisis que no lo fue.

### 5.4 Regeneración

La regeneración tiene su propio pipeline (`PrepareRegenerationStep → SqlFacetExtractorStep → informes → síntesis`,
`AnalysisPipelineFactory.cs:180-184`). Los informes en modo B necesitan el plan y el presupuesto, así que ese pipeline debe leer
los artefactos `_domain_map.json`, `_coverage_plan.json` y `_repository_inventory.json` del disco, o recalcularlos si faltan o si
la huella del grafo cambió. Se resuelve en el plan.

## 6. Criterios de aceptación

Cada uno se verifica con tests (los que no necesitan LLM, con un LLM de prueba) o con una medición real marcada como tal.

1. **A1 Inventario determinista:** dos ejecuciones sobre el mismo repositorio producen el mismo JSON salvo la marca de tiempo.
2. **A2 Mapa de dominios:** cada fichero de producción queda asignado a un subsistema o a `unassigned`; con un LLM de prueba que
   devuelve menos subsistemas de los candidatos, ninguno desaparece del mapa. La detección de comunidades da el mismo resultado en dos
   ejecuciones. Con un repositorio sintético cuya capacidad comparte un término de nombre en dos ramas distantes del árbol de
   paquetes (el caso de Notificaciones, sin sus nombres), queda en un único subsistema y no en dos.
3. **A3 Plan de cobertura:** para cada informe en modo B hay como mucho K subsistemas elegidos, todos los excluidos tienen motivo y la
   salida es idéntica entre ejecuciones.
4. **A4 Presupuesto:** tras `TimeBudgetStep`, todos los pasos registrados tienen el `Timeout` calculado y `TimeoutMiddleware`
   aplica el nuevo valor; si la suma supera el techo global se reduce K y consta un aviso.
5. **A5 Modo B:** con un LLM de prueba y N subsistemas se producen N investigaciones y una consolidación; si una falla, el informe se
   escribe y ese subsistema figura en «no cubierto» con un aviso.
6. **A6 Cobertura validada:** el validador marca un informe en modo B sin apartado «Cobertura» o sin alguno de sus subsistemas.
7. **A7 No regresión:** sobre `java-simple` y `csharp-simple`, las citas válidas no bajan del nivel actual (97,7-100 %,
   `docs/project_notes/asistente-chat-y-enriquecimiento-estado.md`, tabla de `gpt-6-sol`) y los sembrados encontrados no bajan de
   17/18 y 19/22 a mano.
8. **A8 Medición real:** sobre el repositorio Java grande (`C:\Users\juan.rozano\m\Online`, reindexado y enriquecido), el mapa
   de dominios contiene Notificaciones como un único subsistema y el 01 lo trata con sus reglas, estados, eventos y flujos,
   contrastado con el documento funcional de Notificaciones que aporta el usuario (el caso está confirmado como real). Se registran
   el coste, el tiempo y los tokens frente a la ejecución actual. Si no mejora, se dice y se decide con el dato.
9. **A9 Genericidad:** `grep` sobre `templates/` y la configuración sin nombres del repositorio de prueba, y una ejecución sobre
   una copia del sample C# sin cambios de código.

## 7. Decision Log

| # | Decisión | Porqué (y coste aceptado) |
|---|---|---|
| D1 | Los pasos nuevos van tras el enriquecimiento y las facetas, no antes | Necesitan el grafo ya enriquecido para agrupar; el coste es que no pueden orientar al enriquecedor. |
| D2 | Mapa de dominios determinista, el LLM solo nombra y prioriza | Reproducible y medible; un LLM que descubra desde cero reintroduce la variabilidad que se quiere eliminar. Coste: repos sin estructura de paquetes ni referencias resueltas darán clusters pobres. |
| D3 | Modo B (investigación por subsistema) para 01, 04, 05, 06; modo A para el resto | Los cuatro dependen del dominio; los otros cinco son transversales y mecánicos. Coste: más tokens en los cuatro. |
| D4 | Los nueve documentos y sus auditores no cambian de contrato | Decisión del usuario: se analiza qué información y qué método, no se rediseñan los informes. |
| D5 | Texto plano entre pasos, sin JSON | Decisión vigente (`project_report_catalog_decisions`). |
| D6 | Tiempos dinámicos por tamaño de código, escritos por `TimeBudgetStep` en `Timeout` de cada paso | Decisión del usuario: 10 min era un número a ojo. El framework ya lo permite (`StepBase.Timeout` mutable). |
| D7 | Investigación independiente por informe, sin caché compartida | Cada informe aporta su ángulo; coste: 01, 04, 05 y 06 investigan el mismo subsistema cuatro veces. Se mide y se revisa. |
| D8 | Un fallo de investigación es un aviso y un «no cubierto» | No fallar duro (`feedback_no_fail_hard_prefer_warning`). |
| D9 | K = 8 subsistemas por informe y 4 investigaciones en paralelo (aceptados por el usuario el 2026-10-04); coeficientes de tiempo provisionales | Los dos primeros son configurables y se revisan con A7 y A8; los coeficientes no tienen aún datos y se calibran con esas mediciones. |
| D11 | Comunidades de grafo calculadas por el paso, más fusión por vocabulario | cbm no aporta `community_id` en `Online` y el caso real es transversal al árbol de paquetes, así que ni la estructura sola ni la comunidad de cbm sirven. Coste: un algoritmo propio que mantener y que hay que hacer determinista. |
| D12 | La consolidación reutiliza el paso de auditor existente, con dos propiedades nuevas en `ReportFacetInput` | Es el único punto de escritura del informe (fichero, `analysis-meta`, procedencia, `report.written`): reutilizarlo conserva el contrato sin duplicar esa lógica. |
| D10 | Sin expediente JSON, sin crítico adversarial y sin verificador que reabra, por ahora | Es la parte más cara de la propuesta de GPT Sol, choca con el verificador a demanda ya decidido y no se sabe todavía cuánto aporta sobre el descubrimiento por subsistema. |

## 8. Alternatives Considered

- **Pipeline completo de GPT Sol (13 pasos):** descartado en esta tanda por coste, por el bucle de reapertura que contradice la
  verificación a demanda, y porque el redactor sin tools sobre un expediente JSON puede perder el contexto que hoy da citas
  válidas (A7 lo mediría). Lo aprovechable (descubrimiento, cobertura, investigación por subsistema) se incorpora.
- **Solo briefing y protocolo (modo A para todos):** barato, no toca el framework, pero `[I]` el modelo, con un único contexto y un
  límite de iteraciones de tools, puede seguir recorriendo pocos subsistemas. Se conserva solo para los informes transversales.
- **Dominios descubiertos por el LLM desde cero:** más flexible, pero no reproducible y sin forma de comprobar cobertura.
- **Caché de investigación compartida por subsistema:** menos tokens, pero acopla informes; aplazada hasta medir el coste real (D7).
- **Timeouts constantes por rol (la situación actual):** descartado, no escalan con el repositorio.
- **Presupuesto publicado en el contexto y leído por cada paso:** descartado por el usuario a favor de escribir la propiedad
  directamente.

## 9. Open Questions

1. Umbral de fusión por vocabulario y tamaño mínimo de un subsistema: demasiado bajo funde capacidades distintas, demasiado alto
   reproduce el problema del árbol de paquetes. Se calibra sobre `Online` con A8; hasta entonces, valores en configuración.
2. ¿El `DotNetEnricherStep` debe saltarse también en repositorios sin C# ni Java, en vez de caer al fallback del LLM? Arreglo
   aparte de una línea, no bloquea esta spec.
3. ¿Las preguntas de investigación por informe bastan como plantilla genérica o necesitan al LLM? Se decide al ver los primeros
   resultados.
4. ¿Debe el 07 (APIs y seguridad) pasar a modo B si la medición muestra que pierde cobertura por subsistema?
5. Progreso en la UI: cada informe en modo B tiene N investigaciones internas; hay que decidir si la rejilla de auditores las
   muestra o sigue mostrando un único carril por informe.
6. Coeficientes de tiempo del §4.4: se fijan con A7 y A8 (K y la concurrencia ya están aceptados).

## 10. Change Log

- 2026-10-04 (revisión): el usuario confirma K = 8, concurrencia 4 y que el caso Notificaciones es real. Verificado lo que estaba
  abierto: cbm no trae `community_id` en `Online` (el paso calcula sus comunidades), los nodos `Class` llevan `lines: 0` (el
  inventario cuenta líneas en disco), la factoría construye el pipeline en cada ejecución, y el patrón de invocación directa del
  verificador sirve de modelo para el envoltorio de modo B. Se concreta la consolidación (reutiliza el auditor) y se corrigen las
  referencias de línea.
- 2026-10-04: versión inicial, tras el brainstorming con el usuario (modo B para los informes de dominio, mapa determinista con
  LLM que nombra, `TimeBudgetStep` que escribe `Timeout` directamente en los pasos registrados).

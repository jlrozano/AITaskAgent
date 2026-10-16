# Retomar (2026-10-16): bucles de herramientas en el framework y calidad de los informes

Documento de retomada para una sesión limpia. Todo lo de abajo está **sin commitear** (rama `consolidation`). No hay nada a medias en el código: compila, 1397 tests de `CodebaseAnalysis.Core.Tests` verdes.

## 1. Tarea pendiente principal: bucles de herramientas (decisión del usuario: se revisa en sesión limpia)

**Qué pasó** (tercera corrida sobre `Framework`, copia en `C:\Users\juan.rozano\m\fw`, log `logs\analysis-pipeline-20261016.log`, 09:04-10:02):
- El paso `Investigation_AnalisisFuncional_execution` (informe 01, subsistema «Ejecución de pipelines») entró en bucle alternando `view_file Core/Execution/Pipeline.cs` (103 veces) y `view_file YAML/Execution/YamlPipelineFactory.cs` (101 veces), sin texto, hasta agotar las 500 iteraciones. 501 llamadas, 105 M tokens de entrada, **108,59 USD de los 126,22 USD de la corrida** (con caché a 0,20 $/M). El resto del análisis costó ~17,6 USD, como en la corrida anterior (~19,4).
- Las otras tres investigaciones del mismo subsistema (flujos, errores, arquitectura) terminaron en 100-155 s. La petición de la iteración 200 seguía llevando el prompt de la tarea y los dos ficheros ya leídos (67 resultados de herramientas en la ventana): no es falta de contexto.
- **Causa del no-corte:** `Framework/LLM/Steps/BaseLlmStep.LlmInvocation.cs:236-290` (`IsStuckRepeatingToolCalls`) solo compara cada iteración con la **anterior**. Un patrón A, B, A, B nunca es «consecutivo idéntico». Lo frenaba antes el tope de 12 iteraciones, que subí a 500 el 2026-10-15 al quitar límites. **Es un fallo mío de diseño al quitar el tope sin cubrir este caso.**
- Cada llamada del bucle llevaba ~232 000 tokens de entrada (ventana deslizante llena, `SlidingWindowMaxTokens` 262144), ~0,25 USD por llamada, ~1 USD/min.

**Propuesta (pendiente de aprobar, genérica, Framework):**
1. Detector por ventana: parar cuando una misma llamada de solo lectura (clave `LlmHelpers.NormalizeToolCallKey`) aparezca 3 o más veces en las últimas 10 iteraciones (cubre ciclos de cualquier periodo). Mensaje de parada como el actual (`StopWithMessage`) para que el modelo conteste con lo que tiene. Test en el framework (ciclo A,B,A,B y A,B,C,A,B,C).
2. Tope de iteraciones alto pero finito para los auditores (p. ej. 100): en las corridas de 2026-10-15 el 99 % de las investigaciones terminó antes de 80. Cambiar `Auditors:MaxToolIterations` y `MaxToolIterationsByRole` de `Samples/CodebaseAnalysis.Core/appsettings.analysis.json` (hoy 500).
3. Opcional: aviso en el log y en `_run_meta.json` cuando una investigación pasa de N iteraciones (para que el usuario lo vea sin abrir el log).
4. Regla de proyecto (`feedback_framework_extension_points`): no añadir opciones a `Framework/` para un sample. Aquí es un defecto genérico del detector, no una opción de sample; decirlo así al proponerlo.

**Cómo comprobarlo sin LLM real:** test unitario con un `ILlmService` falso que alterne dos llamadas (hay patrones en `Samples/CodebaseAnalysis.Core.Tests`). **Prueba real solo con OK del usuario y tope** (regla `feedback_never_run_cli_with_unverified_flags`). Para repetir el caso: relanzar solo el informe 01 sobre `m\fw` (regenerar informe) con el detector corregido.

## 2. Qué se hizo el 2026-10-15/16 (todo sin commitear)

Código y configuración (detalle en memoria `project_analysis_quality_changes_2026_10_15`):
- `Preparation/SourceSetClassifier.cs`: `tools/scripts/docs...` solo «sin clasificar» en la raíz (antes `LLM/Tools` quedaba fuera de producción: 173 vs 177 ficheros). Aviso de código sin clasificar en `Steps/RepositoryInventoryStep.cs`.
- `Support/ReportMarkdown.cs`: `RemoveTextBeforeTitle` (frases sueltas antes del H1), `FindingIndex`. Aviso en `Reports/ReportValidator.cs`.
- `Reports/SeverityContrast.cs` + `ReportValidator.SummarizeFindings`: discrepancias de severidad entre informes (P0/P1 frente a ≥2 niveles menos sobre el mismo código, ±40 líneas), añadidas al digest de `Steps/ExecutiveSynthesizerStep.cs`.
- Plantillas (`Samples/CodebaseAnalysis.Core/templates/`): reglas comunes (título primero, severidad con camino de activación, una severidad por problema, sin relleno, «sin usos dentro del alcance analizado»), deuda y síntesis sin nota numérica, errores con mecanismos que impiden el efecto, arquitectura sin métricas vacías, preguntas de investigación genéricas nuevas, herramientas de consolidación sin tope.
- `appsettings.analysis.json`: `MaxToolIterations` 500, `Timeouts` todos a 0, `MaxSubsystemsPerReport` 50, `TimeBudget` curvas `{0,0,0,0}` (solo `MaxMinutes: 0` no basta: la curva seguía dando ~7 min por investigación), `gpt-6-sol` con `CachedInputPricePerMillion: 0.2` (dato del usuario). **`gpt-6-sol-global` y los demás modelos siguen sin precio cacheado (no confirmado).**
- Tests actualizados: `PreparationOptionsTests` (50 subsistemas, curvas a cero), `LlmUsageTests` (10,2 con toda la entrada en caché), nuevos `SeverityContrastTests`, `PostAuditCoreTests`, `SourceSetClassifierTests`.

## 3. Resultado de la tercera corrida (informes en `m\fw\reports`)

Mejoras confirmadas: 10 informes sin fallos, 11/11 subsistemas, 177 ficheros, H1 primero en todos, sin nota sobre 100, 05 ya no tiene P0 (el doble `NextSteps.Add` quedó `05-ERR-005` P2 y el activador `05-ERR-003` P2, como verifiqué leyendo `Pipeline.cs`), el 00 recoge «dos discrepancias de severidad que requieren conciliación». Dictamen global Rojo por 3 P1 (`05-SEC-001` lectura de ficheros por includes, `05-ERR-001` escritura no atómica de conversaciones, `05-CON-001` estado de conversación compartido).

Defectos que quedan (del validador: 13 errores, 15 avisos):
- **09 trae las secciones duplicadas** (`09-IOC-001..004` encabezan 2 hallazgos, líneas 49 y 178; hay dos tablas de semáforo y dos «## Hallazgos»): el modelo emitió el informe dos veces (probable concatenación de un borrador anterior a las herramientas con el final). Misma familia que el texto antes del H1. Pendiente: detectar un documento repetido y quedarse con el último, o investigar de dónde sale la concatenación en `CustomStatelessTemplateLlmStep`.
- **Citas con ruta equivocada:** 01 cita `YAML/Execution/SchemaCompiler.cs` (real: `YAML/Compilation/`) y `YAML/Execution/IYamlStep.cs` (real: `YAML/Abstractions/`); 04 cita `LLM/Conversation/Context/MessageHistory.cs` (real: `LLM/Conversation/MessageHistory.cs`). 5 citas rotas en total, 18 de 131 nombres de grafo no encontrados.
- `05-ERR-001` P1 (escritura no atómica) parece condicionado a elegir el almacenamiento de fichero (el registro por defecto es en memoria): revisar si debería ser P2 según la regla «camino de activación».
- Servicio Azure con algún 503 y un timeout de 300 s por intento (reintentados bien).

## 4. Pendiente de otros temas (sin tocar)
- La copia `C:\Users\juan.rozano\m\fw` **sigue sin borrar** (el usuario quería limpiarla al acabar; el borrado me fue denegado: lo ejecuta él con `! rm -rf /c/Users/juan.rozano/m/fw` y `! "$LOCALAPPDATA/Programs/codebase-memory-mcp/codebase-memory-mcp.exe" cli delete_project '{"project":"C-Users-juan.rozano-m-fw"}'`). Los resultados buenos de la primera corrida están en `C:\Users\juan.rozano\eval-repos\results\2026-10-15-framework-gpt6sol\`; los de las corridas 2 y 3 solo están en `m\fw\reports` y `m\fw\logs` (copiarlos antes de borrar si interesan).
- Un servidor viejo (build del 5-oct, puerto 5180) falseó la primera corrida del usuario; ya se paró. Antes de lanzar, comprobar en el primer log `Límites de CodebaseAnalysis:Timeouts` y 177 ficheros.
- Plan final del 2026-10-06 (`docs/plans/2026-10-06-flujo-analisis-plan-final.md`): falta el «adelante» del usuario (contabilidad de caché, protocolo `<nota>`, etc.).
- Aparcado: proveedor sobre Claude Code (memoria `project_claude_code_provider_idea`), Ollama/Gemma local, proveedor Responses API, selección de subsistemas/raíz con `WebContent`, catálogo de dominios, documento funcional por dominio.
- Nada commiteado: ~264 ficheros modificados en `consolidation`. No commitear sin que lo pida.

## 5. Prompt de arranque para la sesión limpia

> Retomamos el análisis de código en `C:\GeminiCli\AITask\AITaskAgent` (rama `consolidation`, todo sin commitear). Lee `docs/project_notes/2026-10-16-bucles-de-herramientas-y-calidad-informes-retomar.md` entero y la memoria `project_analysis_quality_changes_2026_10_15`. Primera tarea: el detector de bucles de herramientas del framework (apartado 1), con tests con LLM falso; no lances análisis reales sin mi OK y un tope. Responde en español, breve, con `[V]/[I]/[?]` y TL;DR final.

Genera el **Informe de Errores, Bugs y Vulnerabilidades con Criticidad y Soluciones** para el repositorio analizado.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Puntos Críticos de Falla (`hotspots` altos + excepciones o manejo de error)**: primero los métodos con hallazgos graves del compilador (`issue_rules`, con su `issues_total` y los ocho primeros en `detected_issues`, que llevan la línea), después los de más `hotspot` o con `throws`. En los principales va el `code_snippet`.
```json
{{ critical_hotspots_exceptions }}
```

- **Rutas por Autenticación (recuentos)**: `without_declared_auth` son las rutas que no declaran autenticación ni tienen política global que la exija (`auth_inferred`); `explicit_allow_anonymous`, las marcadas `[AllowAnonymous]`; `unknown`, las que el grafo no sabe.
```json
{{ route_auth_summary }}
```

- **Rutas que el Grafo Declara sin Autenticación (`auth_required = false`)**: `auth_basis` distingue la que no declara nada (inferido, un hallazgo a confirmar) del `[AllowAnonymous]` explícito (una decisión del código, que solo es hallazgo si la ruta expone algo sensible). Las rutas sin ese dato no aparecen, porque su validación es desconocida; compruébala en el código si la necesitas.
```json
{{ unvalidated_routes }}
```

- **Pantallas (JSP) con Acceso a Datos (SQL, `java.sql`, DAO) o Código de Negocio en Scriptlets (`suspicious`, con sus motivos; `session_access` es un dato aparte y no marca la pantalla)**:
```json
{{ suspicious_jsps }}
```

- **Forwards de Struts sin Destino Resuelto**:
```json
{{ unresolved_forwards }}
```

- **Hallazgos del Compilador por Regla (totales)**: reglas de Roslyn (C#) y javac (Java) aplicadas a todos los métodos y campos: `empty-catch`, `null-deref`, `null-return-deref`, `unawaited-task`, `blocking-async`, `async-void`, `undisposed-resource`, `string-identity`, `unreachable-code`, `unused-field`, `obsolete-api` y `captive-dependency`. En un repositorio grande son miles: aquí van los totales por regla (las graves primero) y no se listan todos.
```json
{{ detected_issues_summary }}
```

- **Hallazgos del Compilador más Relevantes (`suspect_patterns`)**: por regla, los de los métodos con más `hotspot` dentro de un cupo (las reglas graves tienen más cupo). Cada uno lleva su regla, su texto y su cita `ruta:línea`. Son candidatos que detecta el compilador, no pruebas: lee la línea citada antes de convertirlos en un defecto, descarta los que no lo sean y cita siempre `ruta:línea`. Cuando una regla tiene muchos más hallazgos que los mostrados (compara con los totales), reporta el total y los ejemplos verificados, y di cuántos no has revisado.
```json
{{ suspect_patterns }}
```

- **Credenciales escritas como literal en el código (valor enmascarado; extraídas del disco, no del grafo)**:
```json
{{ hardcoded_secrets }}
```

- **Credenciales en claro resueltas por el compilador (valor enmascarado; `resolved_secrets`)**: constantes que javac o Roslyn resuelven (un `static final` o `const`, una concatenación de constantes, la contraseña de `getConnection`, una cadena de conexión con `Password=`), con su cita `ruta:línea`. Es más fiable que la lista de disco para valores que no están escritos en la línea de la asignación; puede repetir entradas de la lista de arriba, y cada línea se cuenta una sola vez. Nunca reproduzcas el valor, ni el enmascarado.
```json
{{ resolved_secrets }}
```

- **Notas de Investigación por Subsistema**: texto plano de investigaciones previas, una por subsistema elegido, con citas `ruta:línea`. Llega vacío cuando no hubo preparación por subsistemas. Nunca contienen valores de credenciales.
```text
{{ investigation_notes }}
```

- **Resumen de Cobertura de la Preparación**: subsistemas cubiertos por una investigación, excluidos con su motivo y fallidos o sin completar. Llega vacío cuando no hubo preparación por subsistemas.
```text
{{ coverage_summary }}
```

---

### Instrucciones de Ejecución:
1. **Inspección Contextual del Código y Adaptación al Lenguaje**:
   - Ajusta la auditoría de defectos a los riesgos típicos del ecosistema detectado (`{{ detected_languages }}`):
     - *En VBA / Microsoft Access*: Inyección SQL por concatenación de cadenas sin sanitizar, manejo de errores silenciado (`On Error Resume Next` sin evaluar `Err.Number`), fugas de recursos COM/ADO/DAO por falta de cierre o de `Set x = Nothing`, mutación descontrolada de filtros dinámicos.
     - *En C# / Java / TypeScript*: Mal uso de asincronía (`async void`, bloqueos sincrónicos `.Result`), condiciones de carrera, capturas genéricas que tragan excepciones (`catch (Exception)` vacíos), desbordamientos o fugas de conexiones.
     - *En Python / JavaScript / Node*: Excepciones capturadas y descartadas, promesas sin `await` ni manejo de rechazo, mutación de estado compartido, deserialización o `eval` sobre entrada externa.
     - *En C / C++ / Go*: Errores de retorno ignorados, desbordamientos de búfer, fugas de memoria o de descriptores, condiciones de carrera entre goroutines o hilos.
     - *En SQL*: Consultas no parametrizadas, bloqueos por transacciones largas, falta de índices en claves foráneas, conversiones implícitas de tipos.
     - *En cualquier otro lenguaje*: los riesgos típicos de su plataforma, con el mismo criterio.
   - Los datos pre-extraídos ya incluyen `file`, `start_line` y `end_line` (y los principales hotspots contienen el `code_snippet` pre-extraído del código fuente).
2. **Credenciales en claro**: evalúa cada entrada leyendo la línea citada.
   - Si el valor es un secreto real (contraseña, clave de API, token, cadena de conexión con contraseña), es un hallazgo de seguridad y se clasifica con la rúbrica del punto 3. Cita el fichero y la línea.
   - Si la entrada es claramente un falso positivo del extractor, porque el identificador nombra una referencia al secreto y no el secreto (un identificador de configuración o de almacén de secretos, un GUID, una ruta, el nombre de una variable de entorno, un marcador de posición), se descarta: no entra en la matriz y se deja constancia en una línea de la sección de alcance o evidencia con el fichero, la línea y el motivo del descarte. Ante la duda, se trata como secreto real.
   - En ningún caso se reproduce el valor, ni siquiera el enmascarado.
3. **Rúbrica de severidad** (aplícala literalmente; el nivel depende del impacto y de lo que hace falta para explotarlo, no del lenguaje ni del tamaño del sistema):
   - **P0 (crítico):** explotable o dañino sin condiciones adicionales. Incluye siempre un secreto real (contraseña, clave, token, cadena de conexión con contraseña) escrito en claro en código o en configuración versionada, aunque el repositorio sea privado o la aplicación sea interna: cualquiera con acceso al código tiene la credencial. También inyección (SQL, comandos) o ejecución de código alcanzable desde entrada externa, y pérdida o corrupción de datos en el flujo normal.
   - **P1 (alto):** impacto grave que necesita una condición adicional: inyección alcanzable solo con acceso previo, credenciales guardadas o transmitidas en claro fuera del código (tablas, ficheros generados, parámetros), errores silenciados que ocultan fallos de datos en procesos de negocio, fugas de recursos o condiciones de carrera que tumban el proceso en uso normal.
   - **P2 (medio):** resultado incorrecto en casos concretos o degradación con impacto acotado y solución local.
   - **P3 (bajo):** mantenibilidad o defectos latentes sin impacto observable hoy.
   - Ante la duda entre dos niveles, se elige el más grave solo si la evidencia muestra la vía de fallo o de explotación; si no, el menos grave, y se dice qué falta para confirmarlo.
   - **Camino de activación demostrado para P0 y P1:** el hallazgo cita con `ruta:línea` el punto desde el que se dispara (quién lo invoca, o dónde se registra, y con qué entrada). Sin ese camino leído en el código, el máximo es P2.
   - **Mecanismos que podrían impedir el efecto:** para cada P0 y P1, el hallazgo dice qué mecanismo del código podría evitar o amortiguar el efecto (un resultado guardado en caché que impide reejecutar, una validación previa, una guarda, un consumidor que nunca llama a ese código) y lo ha leído antes de calificar. Si el efecto se produce de todos modos, lo explica con la línea; si un mecanismo lo impide, el hallazgo baja de nivel. Un elemento que ningún código del alcance llama, o que otro informe del mismo análisis describe como sin usos internos, no es P0 ni P1 sin un consumidor externo concreto que lo active.
   - Un mismo problema lleva la misma severidad en toda la matriz y en el texto. Si el informe usa otra escala en alguna sección (por ejemplo «impacto bajo condicionado»), la cifra de la matriz es la que vale.
   - Una solución propuesta se comprueba contra la firma y el contrato reales: si cambia el contrato público o no compila, el hallazgo lo dice.
4. Para cada defecto explica su impacto y proporciona el fragmento de código de solución exacto para remediación.
5. **Matriz de criticidad**: una única tabla Markdown con una fila por hallazgo y, como mínimo, las columnas `| ID | Severidad | Tipo | Evidencia |`. La celda de severidad contiene exactamente `P0`, `P1`, `P2` o `P3`, y el `ID` es el mismo que encabeza el hallazgo en el detalle. Genera el informe con esta matriz y los diagramas Mermaid requeridos.
6. **Notas de investigación y apartado «Cobertura»** (solo si las notas o el resumen de cobertura traen contenido):
   - Las notas describen defectos candidatos por subsistema, sin severidad: la asignas tú con la rúbrica del punto 3. Son evidencia de segunda mano: incorpora a la matriz los defectos cuya cita y descripción cuadran, y para los dudosos (los de severidad alta que sostengan una conclusión o cuya cita no cuadra) lee la línea citada antes de incluirlos, descartando los que el código no confirme y dejando constancia de los descartes relevantes. Si una nota contradice al código, manda el código y se dice. Lo que las notas marcan como inferido sigue como inferido y no entra en la matriz como defecto confirmado. No copies las notas: intégralas.
   - **Integración completa, sin límite de extensión:** cada nota describe un subsistema con todo lo que su investigación encontró. El informe integra **todo** ese contenido y no un resumen: cada hallazgo, causa, condición de disparo, camino de error, recurso, consecuencia y cita de las notas aparecen en el apartado que les corresponde, reorganizados por tema y escritos una sola vez cuando dos notas dicen lo mismo. No hay límite de longitud: el informe mide lo que mida su contenido, y se prefiere uno largo y completo a uno corto que descarte reglas o datos. Cada hallazgo de las notas se desarrolla con su causa, su condición y su consecuencia; solo se fusionan los que describen exactamente lo mismo. La severidad la asigna la rúbrica de este informe. Cada afirmación conserva la cita que traen las notas.
   - **Presupuesto de herramientas de la consolidación:** esta fase parte de las notas, pero no tiene tope de consultas por diseño. Usa las herramientas siempre que haga falta para comprobar una cita dudosa (una nota que contradice a otra o a las facetas, o cuya cita no cuadra con lo que ves), cubrir un hueco concreto (una pregunta del informe que ninguna nota responde) o confirmar el camino de activación de un hallazgo grave. Lo que no hayas podido comprobar, márcalo como inferido.
   - Un subsistema sin notas (excluido o fallido) no se declara libre de defectos: se declara no cubierto.
   - Añade como último apartado, justo antes de la línea de metadatos, `## Cobertura`, con tres listas: subsistemas cubiertos (una línea con lo que aportan), excluidos con su motivo, y fallidos o incompletos con lo que quedó sin verse. Cada subsistema se nombra exactamente como figura en el resumen de cobertura. Una frase final indica que las conclusiones del informe valen para lo cubierto. Este apartado no define hallazgos, no lleva identificadores y no añade filas a la matriz de criticidad.
   - Si las notas llegan vacías y el resumen de cobertura también, no hubo preparación por subsistemas: trabaja como si estos dos bloques no existieran, con las facetas y las herramientas, y omite el apartado «Cobertura». Si solo trae contenido el resumen de cobertura, redacta el apartado únicamente con lo que dice.
7. En la línea final de metadatos añade `critical=<n>`, donde `<n>` es el número de filas de la matriz con severidad `P0` o `P1` (`0` si no hay ninguna), por ejemplo: `<!-- analysis-meta: health=Red; critical=5 -->`. El pipeline lo recalcula a partir de la matriz y corrige la línea si no cuadra, así que cualquier cifra de P0/P1 que des en el texto (dictamen, conclusión) tiene que ser la de la matriz.

---

{{ include '_provided_documentation.md' }}

**Identificadores de hallazgo de este informe:** el número de informe es `05`. Todos llevan el formato `05-CAT-nnn` (por ejemplo `05-SEC-001`, `05-ERR-002`).

@[_common_rules.md]

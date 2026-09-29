Genera el **Informe de Errores, Bugs y Vulnerabilidades con Criticidad y Soluciones** para el repositorio analizado.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Puntos Críticos de Falla (`hotspots` altos + excepciones o manejo de error)**:
```json
{{ critical_hotspots_exceptions }}
```

- **Rutas que el Grafo Declara sin Autenticación (`auth_required = false`)**: las rutas sin ese dato no aparecen, porque su validación es desconocida; compruébala en el código si la necesitas.
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

- **Uso de Prácticas Peligrosas o Patrones Antipáticos Detectados**:
```json
{{ suspect_patterns }}
```

- **Credenciales escritas como literal en el código (valor enmascarado; extraídas del disco, no del grafo)**:
```json
{{ hardcoded_secrets }}
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
4. Para cada defecto explica su impacto y proporciona el fragmento de código de solución exacto para remediación.
5. **Matriz de criticidad**: una única tabla Markdown con una fila por hallazgo y, como mínimo, las columnas `| ID | Severidad | Tipo | Evidencia |`. La celda de severidad contiene exactamente `P0`, `P1`, `P2` o `P3`, y el `ID` es el mismo que encabeza el hallazgo en el detalle. Genera el informe con esta matriz y los diagramas Mermaid requeridos.
6. En la línea final de metadatos añade `critical=<n>`, donde `<n>` es el número de filas de la matriz con severidad `P0` o `P1` (`0` si no hay ninguna), por ejemplo: `<!-- analysis-meta: health=Red; critical=5 -->`. El pipeline lo recalcula a partir de la matriz y corrige la línea si no cuadra, así que cualquier cifra de P0/P1 que des en el texto (dictamen, conclusión) tiene que ser la de la matriz.

---

{{ include '_provided_documentation.md' }}

@[_common_rules.md]

Genera el **Informe de Inventario de APIs, Seguridad y Superficie de Exposición** para el proyecto analizado.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Catálogo de Rutas, Endpoints o Consultas de Entrada (`label IN ('Route', 'AccessQuery', 'Query')`)**:
```json
{{ routes_catalog }}
```

- **Rutas por Autenticación (recuentos)**: `without_declared_auth` son las rutas que no declaran autenticación ni tienen política global que la exija (`auth_required = false` con `auth_inferred = true`); `explicit_allow_anonymous`, las `[AllowAnonymous]`; `unknown`, las que el grafo no sabe.
```json
{{ route_auth_summary }}
```

- **Mapeo de Rutas y Consultas con Atributos de Seguridad** (`auth_required` vale `desconocido` cuando el grafo no tiene el dato: no significa que la ruta no esté protegida; `auth_required = false` con `auth_inferred = 1` es una ruta que no declara autenticación, y sin `auth_inferred` es un `[AllowAnonymous]` explícito; las rutas sin autenticación van primero). Son datos del compilador que se verifican contra el código de la ruta, con su cita:
```json
{{ routes_security_matrix }}
```

- **Contratos de Entrada y Salida por Ruta o Consulta**:
```json
{{ routes_contracts }}
```

- **Puntos de Exposición Huérfanos o sin Método Asociado**:
```json
{{ orphan_routes_list }}
```

- **Acciones Struts sin Clase que las Atienda (sin `HANDLES`, con el motivo)**:
```json
{{ struts_actions_without_handler }}
```

- **Credenciales escritas como literal en el código (valor enmascarado; extraídas del disco, no del grafo)**:
```json
{{ hardcoded_secrets }}
```

- **Credenciales en claro resueltas por el compilador (valor enmascarado; `resolved_secrets`)**: constantes que javac o Roslyn resuelven (un `static final` o `const`, una concatenación de constantes, la contraseña de `getConnection`, una cadena de conexión con `Password=`), con su cita `ruta:línea`. Es más fiable que la lista de disco para valores que no están escritos en la línea de la asignación; puede repetir entradas de la lista de arriba, y cada línea se cuenta una sola vez. Nunca reproduzcas el valor, ni el enmascarado.
```json
{{ resolved_secrets }}
```

### Preparación del Repositorio (inventario, mapa de dominios y plan de cobertura):
Contexto para repartir el análisis por todo el repositorio. Los tres bloques llegan vacíos (`[]`, `{}` o sin texto) cuando no hubo preparación.

- **Inventario del Repositorio** (tamaño, lenguajes, cobertura del grafo frente al disco y límites de los datos):
```json
{{ repository_inventory }}
```

- **Mapa de Dominios** (subsistemas detectados, con su resumen y los ficheros que los componen):
```json
{{ domain_map }}
```

- **Plan de Cobertura** (subsistemas elegidos por prioridad, y excluidos con su motivo):
```json
{{ coverage_plan }}
```

---

### Instrucciones de Ejecución:
1. **Adaptación a la Superficie de Exposición Real**:
   - Ajusta el análisis al tipo de aplicación detectada (`{{ detected_languages }}`):
     - Si es un **backend HTTP/API REST**: evalúa métodos HTTP (GET/POST/PUT/DELETE), autenticación, autorización (`auth_roles`, `auth_policies`), validación de contratos y exposición pública.
     - Si es una **aplicación de escritorio / cliente local** (VBA / Access, WPF, etc.):
       - La superficie de exposición NO es un servidor HTTP, sino las **conexiones a bases de datos compartidas (puerto TDS 1433 de SQL Server, ODBC), recursos compartidos de red (SMB 445), consultas de datos sensibles sin restricción de roles y cadenas de conexión o credenciales en código**.
       - Audita las consultas pre-extraídas (`AccessQuery`), verificando si exponen datos sensibles o permiten modificaciones sin control de acceso explícito.
2. Una autenticación `desconocido` se comprueba en el código de la clase que atiende la ruta (`graph_neighbors` sobre la ruta, `HANDLES` entrantes) o en la configuración de seguridad del framework; si no puedes verificarla, declárala no evaluable, no como ruta sin protección.
3. Incorpora a la matriz de seguridad las credenciales en claro que sean secretos reales, citando fichero y línea y el recurso que protegen (base de datos, recurso compartido de red, API). Una entrada que es claramente un falso positivo del extractor, porque el identificador nombra una referencia al secreto y no el secreto (un identificador de configuración o de almacén de secretos, un GUID, una ruta, el nombre de una variable de entorno, un marcador de posición), se descarta con una línea que diga el fichero, la línea y el motivo; ante la duda, se trata como secreto real. No reproduzcas nunca el valor, ni siquiera el enmascarado.
4. **Protocolo de Cobertura por Subsistemas** (solo si el plan de cobertura trae subsistemas elegidos):
   - Recorre uno a uno los subsistemas elegidos del plan y examina en cada uno las rutas, consultas o puntos de entrada que expone, su autenticación, sus contratos y las credenciales de su código. No concentres el informe en el subsistema que tenga más rutas: un listado dominado por uno solo no representa el repositorio.
   - Cada subsistema elegido recibe como mínimo una comprobación con herramientas, con su cita `ruta:línea`, o la línea «Sin hallazgos con la evidencia disponible» si lo comprobado está limpio. Lo que no llegues a recorrer se declara como no cubierto, no se rellena por suposición.
   - Añade como último apartado, justo antes de la línea de metadatos, `## Cobertura`, con tres listas: subsistemas recorridos, excluidos por el plan con su motivo, y no recorridos por falta de evidencia o de tiempo. Cada subsistema se nombra exactamente como figura en el mapa de dominios. Una frase final indica que las conclusiones valen para lo recorrido. Este apartado no define hallazgos ni lleva identificadores.
   - Si el inventario, el mapa y el plan llegan vacíos, no hubo preparación: aplica el resto de las instrucciones sin este protocolo y omite el apartado «Cobertura».
5. Emite el informe completo en formato Markdown con sus correspondientes diagramas Mermaid de superficie y arquitectura de seguridad recomendada.

---

{{ include '_provided_documentation.md' }}

**Identificadores de hallazgo de este informe:** el número de informe es `07`. Todos llevan el formato `07-CAT-nnn` (por ejemplo `07-SEC-001`).

@[_common_rules.md]

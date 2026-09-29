Genera el **Informe de Inventario de APIs, Seguridad y Superficie de Exposición** para el proyecto analizado.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Catálogo de Rutas, Endpoints o Consultas de Entrada (`label IN ('Route', 'AccessQuery', 'Query')`)**:
```json
{{ routes_catalog }}
```

- **Mapeo de Rutas y Consultas con Atributos de Seguridad** (`auth_required` vale `desconocido` cuando el grafo no tiene el dato: no significa que la ruta no esté protegida):
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
4. Emite el informe completo en formato Markdown con sus correspondientes diagramas Mermaid de superficie y arquitectura de seguridad recomendada.

---

{{ include '_provided_documentation.md' }}

@[_common_rules.md]

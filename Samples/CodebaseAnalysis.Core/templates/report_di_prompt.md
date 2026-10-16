Genera el **Informe de Auditoría de Inversión de Control (IoC), Inyección de Dependencias y Patrones Creacionales** para el repositorio analizado.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Registros de Inyección de Dependencias / Factorías (`label IN ('DiRegistration', 'Factory', 'Service')`)**:
```json
{{ di_registrations_catalog }}
```

- **Resumen de Registros de DI**: totales por vida útil, registros sin consumidor (`unused_registrations`), los que consume el propio framework (`consumed_by_framework`: `AddHostedService`), los de consumidor desconocido y las dependencias cautivas.
```json
{{ di_summary }}
```

- **Registros de DI sin Consumidor (`unused_registration = true`)**: `consumer_count = 0` lo calcula el compilador (Roslyn) con las referencias exactas al tipo del servicio, sin contar la llamada de registro ni la lista de bases de la implementación. Sin `consumer_count` (el servicio no es del repositorio, o es de un lenguaje sin ese cálculo) se usa el `fan_in` del grafo, que es una pista. Son candidatos, no pruebas: una resolución por reflexión o por `IServiceProvider` no la ve. Confirma cada uno contra el código antes de calificarlo de registro sin consumidor, y cita su fichero y línea:
```json
{{ unused_di_registrations }}
```

- **Dependencias Cautivas (`captive-dependency`)**: un servicio `Singleton` cuyo constructor pide un servicio `Scoped` o `Transient`, de modo que lo retiene toda la vida de la aplicación (Scoped es el error; Transient, una sospecha menor). El texto trae los dos registros con su fichero y línea y la cita del constructor. Verifica con el código del constructor y del registro antes de afirmarlo:
```json
{{ captive_dependencies }}
```

- **Mapeo de Ciclos de Vida (`di_lifetime`) y Relaciones de Resolución (`DI_RESOLVES`, `CREATES`)**:
```json
{{ di_lifetimes_mapping }}
```

- **Puntos de Extensión Detectados (`is_extension_point = true`, `EXTENDS_POINT`)**:
```json
{{ extension_points_catalog }}
```

- **Adaptadores y Conectores de Configuración (`RESOLVES_ADAPTER`)**:
```json
{{ config_adapters_mapping }}
```

- **Candidatos Creacionales y de Composición detectados por nombre** (funciones, métodos, clases o módulos cuyo nombre sugiere creación, ensamblado o conexión: `Create*`, `Build*`, `Make*`, `New*`, `*Factory*`, `*Builder*`, `*Provider*`, `*Container*`, `*Registry*`, `*Connection*`, `*Client*`…; ordenados por `fan_in`). Es independiente del lenguaje: úsalo sobre todo cuando los catálogos anteriores estén vacíos, y confirma cada candidato antes de calificarlo, porque la coincidencia por nombre no garantiza su función:
```json
{{ creational_candidates }}
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
1. **Ajuste Estricto al Lenguaje**:
   - Considera el stack tecnológico detectado: `{{ detected_languages }}`.
   - Si el proyecto cuenta con un contenedor formal de IoC/DI (.NET, Spring, NestJS, etc.), audita los registros, ciclos de vida (Singleton vs Scoped vs Transient), dependencias cautivas y modularidad de extensiones.
   - Si el proyecto **NO** utiliza un contenedor de DI formal (VBA, C, Go, scripts, muchos proyectos Python o JavaScript, etc.), **evalúa los patrones creacionales propios de ese ecosistema**: funciones o clases factoría, builders, constructores que reciben sus dependencias, módulos o clases que centralizan las conexiones a recursos externos (bases de datos, ficheros, red), acoplamiento de instanciación en la capa de interfaz y la gestión/liberación del ciclo de vida de los recursos según el mecanismo del lenguaje (liberación explícita, `defer`, `with`/`using`, destructores, cierre de conexiones). NO penalices la ausencia de un contenedor .NET/Java si el proyecto no pertenece a ese ecosistema.
2. **Protocolo de Cobertura por Subsistemas** (solo si el plan de cobertura trae subsistemas elegidos):
   - Recorre uno a uno los subsistemas elegidos del plan y examina en cada uno sus registros de DI, factorías, puntos de creación y ciclos de vida de recursos (o los patrones creacionales propios del ecosistema si no hay contenedor). No concentres el informe en el subsistema que tenga más registros: un listado dominado por uno solo no representa el repositorio.
   - Cada subsistema elegido recibe como mínimo una comprobación con herramientas, con su cita `ruta:línea`, o la línea «Sin hallazgos con la evidencia disponible» si lo comprobado está limpio. Lo que no llegues a recorrer se declara como no cubierto, no se rellena por suposición.
   - Añade como último apartado, justo antes de la línea de metadatos, `## Cobertura`, con tres listas: subsistemas recorridos, excluidos por el plan con su motivo, y no recorridos por falta de evidencia o de tiempo. Cada subsistema se nombra exactamente como figura en el mapa de dominios. Una frase final indica que las conclusiones valen para lo recorrido. Este apartado no define hallazgos ni lleva identificadores.
   - Si el inventario, el mapa y el plan llegan vacíos, no hubo preparación: aplica el resto de las instrucciones sin este protocolo y omite el apartado «Cobertura».
3. Emite el informe completo en formato Markdown con sus correspondientes diagramas Mermaid, tabla semáforo y plan de acción pragmático.

---

{{ include '_provided_documentation.md' }}

**Identificadores de hallazgo de este informe:** el número de informe es `09`. Todos llevan el formato `09-CAT-nnn` (por ejemplo `09-IOC-001`).

@[_common_rules.md]

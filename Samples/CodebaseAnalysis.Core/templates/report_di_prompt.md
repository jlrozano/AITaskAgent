Genera el **Informe de Auditoría de Inversión de Control (IoC), Inyección de Dependencias y Patrones Creacionales** para el repositorio analizado.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Registros de Inyección de Dependencias / Factorías (`label IN ('DiRegistration', 'Factory', 'Service')`)**:
```json
{{ di_registrations_catalog }}
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

---

### Instrucciones de Ejecución:
1. **Ajuste Estricto al Lenguaje**:
   - Considera el stack tecnológico detectado: `{{ detected_languages }}`.
   - Si el proyecto cuenta con un contenedor formal de IoC/DI (.NET, Spring, NestJS, etc.), audita los registros, ciclos de vida (Singleton vs Scoped vs Transient), dependencias cautivas y modularidad de extensiones.
   - Si el proyecto **NO** utiliza un contenedor de DI formal (VBA, C, Go, scripts, muchos proyectos Python o JavaScript, etc.), **evalúa los patrones creacionales propios de ese ecosistema**: funciones o clases factoría, builders, constructores que reciben sus dependencias, módulos o clases que centralizan las conexiones a recursos externos (bases de datos, ficheros, red), acoplamiento de instanciación en la capa de interfaz y la gestión/liberación del ciclo de vida de los recursos según el mecanismo del lenguaje (liberación explícita, `defer`, `with`/`using`, destructores, cierre de conexiones). NO penalices la ausencia de un contenedor .NET/Java si el proyecto no pertenece a ese ecosistema.
2. Emite el informe completo en formato Markdown con sus correspondientes diagramas Mermaid, tabla semáforo y plan de acción pragmático.

---

{{ include '_provided_documentation.md' }}

@[_common_rules.md]

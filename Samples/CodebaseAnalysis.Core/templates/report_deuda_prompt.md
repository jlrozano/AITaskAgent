Genera el **Informe de Matriz de Deuda Técnica y Priorización de Refactorización** para el repositorio analizado, aplicando un análisis riguroso y pragmático ajustado a su ecosistema tecnológico.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Ranking de Hotspots (`hotspot`, `cyclomatic`, `fan_in`)**:
```json
{{ hotspots_ranking }}
```

- **Cobertura Documental por Componente (`doc_coverage`)**: en C# y Java `documented` cuenta solo la documentación reconocida (`docstring_source` `xml` o `javadoc`); `documented_as_comment` son los comentarios `//` anteriores a la declaración y `cbm_docstring_unverified` los `docstring` sin fuente que dejó el indexador (no son documentación fiable). En el resto de lenguajes `documented` cuenta el `docstring` del indexador.
```json
{{ doc_coverage_metrics }}
```

- **Marcas TODO / FIXME / HACK**: totales, por tipo, ficheros con más y las más graves (`FIXME` y `HACK` primero) con su cita `ruta:línea`. Deuda declarada por el propio equipo; en un repositorio grande no se listan todas, da los totales y los ejemplos verificados.
```json
{{ todo_markers }}
```

- **Código Comentado**: comentarios que el compilador parsea como código sin errores (no son prosa): bloques, líneas, ficheros con más y los bloques mayores con su cita. Son candidatos: lee el bloque antes de citarlo.
```json
{{ commented_code }}
```

- **Elementos Obsoletos o Deprecados (`obsolete_elements`)**:
```json
{{ obsolete_elements }}
```

- **Clases o Módulos con Métricas Agregadas Extremas (`loc_total`, `cyclomatic_total`)**: `loc_source` y `cyclomatic_total_source` dicen si el valor es propiedad del grafo o un cálculo (rango de líneas, suma de la complejidad de sus métodos).
```json
{{ complex_classes }}
```

- **Pantallas (JSP) con Acceso a Datos (SQL, `java.sql`, DAO) o Código de Negocio en Scriptlets (`suspicious`, con sus motivos; `session_access` es un dato aparte y no marca la pantalla)**:
```json
{{ suspicious_jsps }}
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
1. **Auditoría Pragmática de Hotspots y Adaptación al Lenguaje**:
   - Considera el stack tecnológico detectado: `{{ detected_languages }}`.
   - Cruza complejidad (`cyclomatic`), uso (`fan_in`) y volumen de código para identificar los puntos de mayor concentración.
   - Los hotspots pre-extraídos ya incluyen `file`, `start_line`, `end_line` y los fragmentos `code_snippet` pre-extraídos. Discierne su naturaleza:
     - ¿Es un **Núcleo Cohesivo** con complejidad esencial requerida por el negocio o protocolo? Si es así, clasifícalo como núcleo estable a blindar con tests o validaciones, no como un fallo crítico que deba reescribirse.
     - ¿Es **Deuda Accidental** por mal diseño, código duplicado, falta de modularidad o responsabilidades dispersas? En este caso, evalúa el esfuerzo de saneamiento y el ROI real.
   - Aplica la regla anti-dogma: descarta propuestas que pretendan crear sobre-abstracciones artificiales o despieces vacíos 1:1.
   - Mide el radio de impacto de cada hotspot con `graph_neighbors` (quién lo llama y a quién llama) antes de estimar el esfuerzo de saneamiento; una pantalla sospechosa es deuda solo si su código confirma el motivo.

2. **Categorización de la Deuda en 3 Niveles Reales**:
   - Organiza los hallazgos en:
     - **Nivel 1 (Arquitectura / Módulos)**: Desacoplamiento de capas y fronteras modulares.
     - **Nivel 2 (Diseño / Composición)**: Composición sobre herencia y encapsulación de colaboradores.
     - **Nivel 3 (Implementación / Algoritmos)**: Estructuras de datos limpias, simplificación de algoritmos y eliminación de duplicidades.

3. **Matriz de Priorización y Plan de Saneamiento por Sprints**:
   - Formula la hoja de ruta estimando el esfuerzo en **Días-Hombre (d/h)** realistas para cada hito.
   - Incluye los 2 diagramas Mermaid requeridos, con los tipos que admiten las directrices de Mermaid:
     - **Matriz Esfuerzo / Impacto de Hotspots** como `flowchart LR` con cuatro `subgraph`, uno por cuadrante (impacto alto y esfuerzo bajo, impacto alto y esfuerzo alto, impacto bajo y esfuerzo bajo, impacto bajo y esfuerzo alto), que contienen los identificadores de los hallazgos. No uses `quadrantChart`.
     - **Hoja de Ruta por Sprints** como `flowchart LR`, un `subgraph` por sprint con sus hitos en orden.

4. **Protocolo de Cobertura por Subsistemas** (solo si el plan de cobertura trae subsistemas elegidos):
   - Recorre uno a uno los subsistemas elegidos del plan y examina en cada uno sus hotspots, las marcas TODO/FIXME/HACK, el código comentado y los elementos obsoletos. No concentres el informe en el subsistema que tenga más datos pre-extraídos: un listado dominado por uno solo no representa el repositorio, y la deuda de los demás quedaría fuera de la matriz de priorización.
   - Cada subsistema elegido recibe como mínimo una comprobación con herramientas, con su cita `ruta:línea`, o la línea «Sin hallazgos con la evidencia disponible» si lo comprobado está limpio. Lo que no llegues a recorrer se declara como no cubierto, no se rellena por suposición.
   - Añade como último apartado, justo antes de la línea de metadatos, `## Cobertura`, con tres listas: subsistemas recorridos, excluidos por el plan con su motivo, y no recorridos por falta de evidencia o de tiempo. Cada subsistema se nombra exactamente como figura en el mapa de dominios. Una frase final indica que las conclusiones valen para lo recorrido. Este apartado no define hallazgos ni lleva identificadores.
   - Si el inventario, el mapa y el plan llegan vacíos, no hubo preparación: aplica el resto de las instrucciones sin este protocolo y omite el apartado «Cobertura».

---

{{ include '_provided_documentation.md' }}

**Identificadores de hallazgo de este informe:** el número de informe es `08`. Todos llevan el formato `08-CAT-nnn` (por ejemplo `08-IMP-001`, `08-DIS-001`).

@[_common_rules.md]

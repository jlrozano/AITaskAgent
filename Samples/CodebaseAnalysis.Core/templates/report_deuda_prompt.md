Genera el **Informe de Matriz de Deuda Técnica y Priorización de Refactorización** para el repositorio analizado, aplicando un análisis riguroso y pragmático ajustado a su ecosistema tecnológico.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Ranking de Hotspots (`hotspot`, `cyclomatic`, `fan_in`)**:
```json
{{ hotspots_ranking }}
```

- **Cobertura Documental por Componente (`doc_coverage`)**:
```json
{{ doc_coverage_metrics }}
```

- **Elementos Obsoletos o Deprecados (`obsolete_elements`)**:
```json
{{ obsolete_elements }}
```

- **Clases o Módulos con Métricas Agregadas Extremas (`loc_total`, `cyclomatic_total`)**: `loc_source` y `cyclomatic_total_source` dicen si el valor es propiedad del grafo o un cálculo (rango de líneas, suma de la complejidad de sus métodos).
```json
{{ complex_classes }}
```

- **Pantallas (JSP) con Código de Negocio, SQL o Sesión (`suspicious`, con sus motivos)**:
```json
{{ suspicious_jsps }}
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
   - Incluye los 2 diagramas Mermaid requeridos (Matriz Cuadrante de Hotspots y Hoja de Ruta por Sprints).

---

{{ include '_provided_documentation.md' }}

@[_common_rules.md]

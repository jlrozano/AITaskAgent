Genera el **Informe de Auditoría de Code Smells y Mantenibilidad** para el proyecto analizado, aplicando con rigor el criterio de Arquitecto Senior y Pragmatismo Funcional.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Top Métodos o Procedimientos por Complejidad Ciclomática (`cyclomatic`)**:
```json
{{ high_cyclomatic_methods }}
```

- **Top Clases o Módulos por Tamaño y Número de Métodos (`loc_total`, `method_count`)** (`loc_source` y `method_count_source` dicen si el valor es propiedad del grafo o un cálculo desde el rango de líneas o las aristas `DEFINES_METHOD`):
```json
{{ bloated_classes }}
```

- **Métricas de Acoplamiento y Dependencias (`fan_in`, `fan_out`)** (`fan_in_source` y `fan_out_source` dicen si el valor es propiedad del grafo o se calcula de las aristas `CALLS` resueltas; sin dato no hay fila):
```json
{{ coupling_metrics }}
```

- **Recuento de duplicación candidata**: pares de métodos que el indexador marca como casi idénticos, con los umbrales y la salvedad.
```json
{{ duplicate_summary }}
```

- **Pares de métodos casi idénticos (candidatos a duplicación)**: las dos ubicaciones (`file_a`/`start_line_a`/`end_line_a` y `file_b`/…), la similitud (1 = idénticos) y si están en el mismo fichero. Confírmalos leyendo el código: una diferencia mínima entre ellos (un sufijo, un símbolo, un literal) no los hace distintos. Si la lista está vacía, no afirmes ni niegues duplicación por esta vía.
```json
{{ duplicate_candidates }}
```

- **Campos privados que nada lee (sin `READS`)**: campos `private` de C# y de Java que no son `static final` ni `const`, con su fichero y línea; `writes` cuenta los métodos que los escriben. Un campo escrito y nunca leído es estado inútil; confírmalo en el código, porque la reflexión, la serialización y los frameworks que usan campos por nombre (`ActionForm` de Struts, beans) no dejan `READS`.
```json
{{ unread_fields }}
```

- **Recuento de campos sin leer**: `never_touched` (nadie los lee ni los escribe) y `write_only` (se escriben y no se leen).
```json
{{ unread_fields_summary }}
```

- **Campos solo escritos (`writes > 0`, sin `READS`)**: estado que se guarda y nadie consulta, como un colaborador inyectado en el constructor que no se usa.
```json
{{ write_only_fields }}
```

- **Campos sin leer agrupados por un patrón común** del tipo que los declara o de su directorio (resumidos: patrón, recuento, tres ejemplos; no se repiten en la lista anterior). Un grupo grande suele ser de campos que se escriben o leen por reflexión o serialización (formularios, beans, DTO): compruébalo antes de tratarlos como estado inútil.
```json
{{ unread_field_groups }}
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

### Metodología de Ejecución del Auditor:

1. **Inspección Contextual del Código y Adaptación al Lenguaje**:
   - Considera el stack tecnológico detectado: `{{ detected_languages }}`.
   - Cada elemento pre-extraído incluye `file`, `start_line` y `end_line` (y los principales métodos/procedimientos incluyen además su `code_snippet` real pre-extraído del código fuente).
   - **No juzgues solo por las métricas numéricas**: Determina si el componente encapsula un único motor de ejecución/proceso (*Engine Cohesivo*) o si mezcla múltiples dominios de negocio desconectados (*God Class / God Procedure Real*).
   - Identifica si la complejidad ciclomática es **Esencial** (impuesta por reglas de negocio, cálculos de protocolo o procesamiento de datos) o **Accidental** (estructuras confusas, lógica duplicada, concatenación manual insegura, branching innecesario).

2. **Aplicación Estricta de la Regla Anti-Dogma (No Lasagna Code)**:
   - **Bajo ninguna circunstancia propongas micro-interfaces cosméticas 1:1** o sobre-abstracciones artificiales sin polimorfismo que fragmenten un componente cohesivo.
   - Justifica cualquier propuesta de refactorización asegurando que reduce la deuda real sin introducir acoplamiento temporal ni trampolín de parámetros.

3. **Estructura Obligatoria del Informe**:
   - **Todo componente examinado es un hallazgo con identificador.** Cada método, clase o par de métodos que analices (incluidos los de complejidad esencial o de motor cohesivo que decidas conservar) se presenta como un hallazgo `02-CAT-nnn` (por ejemplo `02-CPX-001` complejidad, `02-GOD-001` clase con dominios mezclados, `02-COH-001` componente cohesivo que se conserva, `02-DUP-001` duplicación), con su ficha propia y su cita `ruta:inicio-fin` (el rango completo de líneas del dato). Un componente que solo se menciona en prosa, en una tabla sin ID o en un diagrama no cuenta como analizado.
   - **Cobertura mínima de los datos pre-extraídos**: cada método de la lista de complejidad con `cyclomatic` ≥ 15 (y, si ninguno llega, los tres primeros), cada clase señalada por tamaño o por número de métodos que justifique inspección, y cada par de la lista de duplicación se resuelve en un hallazgo con ID: confirmado como smell o conservado con su motivo. Si el dato falta o la lista está vacía, dilo en el resumen en lugar de omitir la categoría.
   - **Resumen Ejecutivo**: Diagnóstico global de mantenibilidad en el contexto del lenguaje del proyecto, destacando la diferencia entre motores de alta cohesión y focos reales de deuda técnica.
   - **Diagrama Mermaid Semántico**: Radar visual clasificando los componentes examinados (Motores Cohesivos, Deuda Accidental, God Classes/Procedures Reales).
   - **Diagnóstico Detallado de Componentes**:
     - *Distinción explícita*: Agrupa el análisis diferenciando claramente entre:
       1. **Runtimes y Motores Especializados (Complejidad Esencial Justificada)**: Módulos o procedimientos cuyo tamaño responde a requerimientos reales del problema.
       2. **Code Smells y Complejidad Accidental Confirmada**: Focos donde sí existe acoplamiento indebido, mezcla de dominios o código duplicado.
     - *Para cada componente analizado, incluye*:
       - **Diagnóstico Técnico**: Cohesión funcional vs dominios implicados.
       - **Matriz de Riesgo Multidimensional**: Riesgo Operativo, Riesgo Evolutivo y Balance Coste/Beneficio (ROI).
       - **Propuesta de Mejora en 3 Niveles Reales**:
         - *Nivel 1 (Arquitectura / Módulos)*: Límites modulares y separación de responsabilidades.
         - *Nivel 2 (Diseño / Composición)*: Composición sobre herencia, modularización o extracción de colaboradores internos.
         - *Nivel 3 (Implementación)*: Estructuras de datos limpias, simplificación de algoritmos y eliminación de duplicidades.
   - **Conclusiones y Plan de Remediación Priorizado**: Roadmap pragmático con ROI positivo, priorizando intervenciones de bajo riesgo operativo y alto beneficio evolutivo.

4. **Protocolo de Cobertura por Subsistemas** (solo si el plan de cobertura trae subsistemas elegidos):
   - Recorre uno a uno los subsistemas elegidos del plan y busca en cada uno sus métodos y clases de mayor complejidad, tamaño o acoplamiento y sus duplicaciones, con `graph_search`, `graph_neighbors` y la lectura del código. No concentres el informe en el subsistema que tenga más datos pre-extraídos: un listado dominado por uno solo no representa el repositorio.
   - Cada subsistema elegido recibe como mínimo una comprobación con herramientas, con su hallazgo y su cita `ruta:línea`, o la línea «Sin hallazgos con la evidencia disponible» si lo comprobado está limpio. Lo que no llegues a recorrer se declara como no cubierto, no se rellena por suposición.
   - Añade como último apartado, justo antes de la línea de metadatos, `## Cobertura`, con tres listas: subsistemas recorridos, excluidos por el plan con su motivo, y no recorridos por falta de evidencia o de tiempo. Cada subsistema se nombra exactamente como figura en el mapa de dominios. Una frase final indica que las conclusiones valen para lo recorrido. Este apartado no define hallazgos ni lleva identificadores.
   - Si el inventario, el mapa y el plan llegan vacíos, no hubo preparación: aplica el resto de la metodología sin este protocolo y omite el apartado «Cobertura».

---

{{ include '_provided_documentation.md' }}

**Identificadores de hallazgo de este informe:** el número de informe es `02`. Todos llevan el formato `02-CAT-nnn` (por ejemplo `02-CPX-001`, `02-GOD-001`).

@[_common_rules.md]

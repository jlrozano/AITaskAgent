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

- **Campos privados que nada lee (sin `READS`)**: campos Java `private` que no son `static final`, con su fichero y línea; `writes` cuenta los métodos que los escriben. Un campo escrito y nunca leído es estado inútil; confírmalo en el código, porque la reflexión, la serialización y los frameworks que usan campos por nombre (`ActionForm` de Struts, beans) no dejan `READS`.
```json
{{ unread_fields }}
```

- **Campos sin leer agrupados por un patrón común** del tipo que los declara o de su directorio (resumidos: patrón, recuento, tres ejemplos; no se repiten en la lista anterior). Un grupo grande suele ser de campos que se escriben o leen por reflexión o serialización (formularios, beans, DTO): compruébalo antes de tratarlos como estado inútil.
```json
{{ unread_field_groups }}
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

---

{{ include '_provided_documentation.md' }}

@[_common_rules.md]

Genera el documento consolidado **`00_resumen_ejecutivo.md`** a partir de los resultados de los informes técnicos especializados, aplicando criterio de Arquitecto Principal y Director de Ingeniería.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Informes Técnicos Ejecutados y sus Conclusiones Principales:
```markdown
{{ generated_reports_digest }}
```

### Métricas Consolidadas del Grafo:
- Total Nodos: `{{ total_nodes }}`
- Total Aristas: `{{ total_edges }}`
- Total Líneas de Código (LOC): `{{ total_loc }}`
- Total Rutas / Consultas Expuestas: `{{ total_routes }}`
- Total Flujos de Negocio Mapeados: `{{ total_flows }}`
- Candidatos a Código Muerto: `{{ dead_code_count }}`
- Defectos Críticos (P0/P1): `{{ critical_bugs_count }}`

### Cobertura de los Informes (preparación por subsistema):
Qué parte del repositorio recorrió cada informe: subsistemas investigados, excluidos con su motivo y no cubiertos, con el apartado «Cobertura» de cada informe. Llega vacío cuando no hubo preparación por subsistemas.
```text
{{ coverage_summary }}
```

---

### Instrucciones de Ejecución:
1. **Análisis Sistémico Transversal y Contextualizado**:
   - Ajusta tu análisis al ecosistema tecnológico real (`{{ detected_languages }}`).
   - Sintetiza las conclusiones de los informes técnicos especializados identificando patrones sistémicos reales.
   - Pondera adecuadamente la salud del código según el tipo de aplicación (ej. aplicación de escritorio con base de datos propia frente a API distribuida en la nube).
   - Distingue la complejidad esencial de los procesos de negocio de la deuda accidental evitable.
2. **Top 5 Acciones Prioritarias en 3 Niveles**:
   - Define las 5 iniciativas de mayor ROI estructuradas en:
     - Nivel 1: Arquitectura y Módulos (fronteras entre capas, UI, lógica de negocio y datos).
     - Nivel 2: Diseño (patrones creacionales, desacoplamiento y encapsulación).
     - Nivel 3: Implementación (simplificación de consultas SQL, manejo robusto de errores, estructuras limpias).
3. **Fidelidad a los Informes de Origen**:
   - **Identificadores y severidades solo de los informes:** cada informe del resumen de arriba trae su lista «Hallazgos del informe (identificador · severidad · tipo)». Cuando cites un hallazgo, usa un identificador de esa lista, con su severidad y con el tipo que figura en ella: no escribas un identificador de memoria ni lo asignes a un problema distinto del suyo. Si el problema que quieres citar no está en ninguna lista, descríbelo sin identificador y di en qué informe se trata. Si dos informes valoran el mismo problema con severidades distintas, dilo en el resumen y no elijas una en silencio.
   - **Discrepancias de severidad:** si el resumen de arriba trae «Discrepancias de severidad entre informes», cada línea es un hallazgo P0 o P1 que otro informe califica menos grave sobre el mismo código. No uses un hallazgo en discrepancia como causa única del semáforo global ni como primera intervención del Top 5 sin decirlo: nombra los dos identificadores, indica que las severidades no coinciden y explica qué informe aporta más evidencia (el que cita el camino de activación o el mecanismo que impide el efecto) o declara que no se puede decidir con lo recibido. El semáforo global se justifica con los hallazgos sin discrepancia y menciona los demás como pendientes de confirmar.
   - **Cifras de defectos críticos:** la cifra de P0/P1 es la de los datos de arriba. Si el informe de errores declara 0 defectos P0, el resumen no habla de «P0/P1» sino de los P1.
   - El semáforo de cada informe es el que figura en el resumen de arriba; no lo recalifiques sin justificarlo con hallazgos del propio informe.
   - Si un informe procede de una ejecución anterior (campo **Origen**), inclúyelo igualmente y señálalo en el índice.
   - Los defectos críticos (P0/P1) son los declarados por el informe de errores. Si la métrica figura como "no disponible", no afirmes que hay cero defectos críticos.
   - **Cobertura:** si el resumen de cobertura trae subsistemas excluidos, sin investigar o no cubiertos, el resumen ejecutivo no presenta el análisis como completo ni exhaustivo: dice en una línea del índice o de las conclusiones qué quedó fuera (los subsistemas que se nombran en el resumen de cobertura, con el motivo) y que las conclusiones valen para lo cubierto. No inventes subsistemas que no figuren en él ni afirmes que algo no cubierto está sano o limpio. Un informe que no declara su cobertura se señala como tal. Si el resumen de cobertura llega vacío, no hubo preparación por subsistemas: no menciones la cobertura.
4. **Elaboración del Informe**:
   - El documento empieza con un único título de nivel 1 que lo describe (por ejemplo, `# Resumen ejecutivo`); el nombre del fichero no es un título.
   - Genera el informe ejecutivo en Markdown con la tabla semáforo de áreas, los diagramas Mermaid requeridos (Radar semántico y topología modular del sistema) y el índice de navegación a los informes técnicos.

---

@[_common_rules.md]

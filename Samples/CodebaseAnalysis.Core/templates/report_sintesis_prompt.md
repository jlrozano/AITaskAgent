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
   - El semáforo de cada informe es el que figura en el resumen de arriba; no lo recalifiques sin justificarlo con hallazgos del propio informe.
   - Si un informe procede de una ejecución anterior (campo **Origen**), inclúyelo igualmente y señálalo en el índice.
   - Los defectos críticos (P0/P1) son los declarados por el informe de errores. Si la métrica figura como "no disponible", no afirmes que hay cero defectos críticos.
4. **Elaboración del Informe**:
   - El documento empieza con un único título de nivel 1 que lo describe (por ejemplo, `# Resumen ejecutivo`); el nombre del fichero no es un título.
   - Genera el informe ejecutivo en Markdown con la tabla semáforo de áreas, los diagramas Mermaid requeridos (Radar semántico y topología modular del sistema) y el índice de navegación a los informes técnicos.

---

@[_common_rules.md]

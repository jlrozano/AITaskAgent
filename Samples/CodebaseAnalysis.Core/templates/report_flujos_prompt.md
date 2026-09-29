Genera el **Informe de Flujos End-to-End y Procesos de Negocio** para el repositorio analizado.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Nodos de Flujo y Procesos Registrados (`label = 'Flow'`)**:
```json
{{ flows_catalog }}
```

- **Grafo de Pasos y Relaciones (`FLOW_ENTRY`, `FLOW_NEXT`, `CALLS`)**:
```json
{{ flow_steps_graph }}
```

- **Mapeo de Rutas, Eventos o Formularios a Métodos de Entrada (`HANDLES`, `TRIGGERS`)**:
```json
{{ route_handlers_mapping }}
```

- **Forwards de Struts sin Destino Resuelto (acción, forward y ruta que no se encontró)**:
```json
{{ unresolved_forwards }}
```

- **Resolución de Dependencias y Servicios Clave**:
```json
{{ di_resolutions }}
```

- **Pasos de Proceso o Workflow Detectados (`WorkflowStep`)**:
```json
{{ workflow_steps_catalog }}
```

- **Documentación y Plantillas Asociadas**:
```json
{{ documentation_and_prompts }}
```

---

### Instrucciones de Ejecución:
1. Revisa la secuencia de pasos y ramas de cada flujo de negocio considerando el stack tecnológico (`{{ detected_languages }}`).
2. **Reconstrucción del Camino Crítico**:
   - Analiza los flujos de ejecución principales de la aplicación analizada: desde los puntos de entrada (eventos de formularios UI, peticiones HTTP, triggers de base de datos o comandos CLI) hasta la persistencia (consultas SQL, transacciones, generación de informes o ficheros).
   - Documenta qué datos viajan en cada paso y dónde se toman decisiones lógicas.
   - Si no hay nodos de flujo, reconstruye el camino con `graph_neighbors`: desde la pantalla, la acción a la que envía (`SUBMITS_TO`); desde la acción, la clase que la atiende (`HANDLES`) y las pantallas a las que reenvía (`FORWARDS_TO`); desde el método de entrada, sus llamadas (`CALLS`, con las líneas). Usa las aristas resueltas como camino y confirma las heurísticas en el código.
3. **Diagramas Mermaid Obligatorios**:
   - Diagrama con Mermaid (`sequenceDiagram` y `flowchart`) los flujos principales del sistema.
   - Aplica estilos explícitos y entrecomilla los participantes.
4. Elabora el informe con diagramas Mermaid representativos y vistosos según las directrices establecidas.

---

{{ include '_provided_documentation.md' }}

@[_common_rules.md]

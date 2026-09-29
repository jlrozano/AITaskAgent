Genera el **Informe de Análisis Funcional: ¿Qué hace la aplicación?** para el repositorio analizado.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Resumen de Métricas Generales**:
```json
{{ metrics_summary }}
```

- **Rutas, Consultas o Puntos de Entrada**:
```json
{{ routes_summary }}
```

- **Procesos y Flujos de Negocio Enriquecidos**:
```json
{{ flows_summary }}
```

- **Entidades de Dominio, Tablas y Modelos**:
```json
{{ domain_entities_summary }}
```

- **Pasos del Workflow y Pipeline Enriquecidos (`WorkflowStep`)**:
```json
{{ workflow_steps_catalog }}
```

- **Templates de Prompts Markdown (.md) y Documentación (`PromptTemplate`)**:
```json
{{ prompt_templates_catalog }}
```

---

### Instrucciones de Ejecución:
1. Revisa las facetas pre-extraídas considerando el ecosistema del proyecto (`{{ detected_languages }}`).
2. Analiza detalladamente la misión funcional del software, sus casos de uso y los procesos de negocio a los que da soporte.
3. Examina detalladamente los flujos y modelos identificados, explicando qué hace cada componente, a qué usuarios o procesos sirve y cómo se orquesta la operativa. Para seguir un caso de uso desde su punto de entrada, usa `graph_domain` (`view: routes`) y `graph_neighbors` sobre la ruta y su clase (quién la atiende, a qué llama y qué pantalla muestra).
4. Redacta el informe completo en formato Markdown estructurado según las directrices establecidas.

---

{{ include '_provided_documentation.md' }}

@[_common_rules.md]

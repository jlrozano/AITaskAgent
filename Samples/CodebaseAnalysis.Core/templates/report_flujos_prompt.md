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

- **Notas de Investigación por Subsistema**: texto plano de investigaciones previas, una por subsistema elegido, con citas `ruta:línea`. Llega vacío cuando no hubo preparación por subsistemas.
```text
{{ investigation_notes }}
```

- **Resumen de Cobertura de la Preparación**: subsistemas cubiertos por una investigación, excluidos con su motivo y fallidos o sin completar. Llega vacío cuando no hubo preparación por subsistemas.
```text
{{ coverage_summary }}
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
4. **Citas de código:** cada paso del camino crítico (el punto de entrada, cada llamada relevante, cada decisión y la operación terminal) lleva su cita `ruta:línea`, tomada de las aristas con línea, de los datos pre-extraídos o de lo que leas con las herramientas. Un paso reconstruido por heurística sin cita se marca como inferido.
5. **Notas de investigación y apartado «Cobertura»** (solo si las notas o el resumen de cobertura traen contenido):
   - Las notas son evidencia de segunda mano: contienen flujos ya reconstruidos por subsistema. Úsalas como base del camino crítico, contrástalas con las facetas y no des por confirmado un paso que contradiga a otra nota o a las facetas. Si una nota contradice al código, manda el código y se dice. Lo que las notas marcan como inferido sigue como inferido. No las copies: intégralas en el análisis y une los flujos que cruzan la frontera de un subsistema a otro.
   - **Integración completa, sin límite de extensión:** cada nota describe un subsistema con todo lo que su investigación encontró. El informe integra **todo** ese contenido y no un resumen: cada paso, rama alternativa, condición, estado, excepción, frontera entre subsistemas y cita de las notas aparecen en el apartado que les corresponde, reorganizados por tema y escritos una sola vez cuando dos notas dicen lo mismo. No hay límite de longitud: el informe mide lo que mida su contenido, y se prefiere uno largo y completo a uno corto que descarte reglas o datos. Cada flujo se desarrolla completo, de la entrada a la persistencia, con sus ramas alternativas y de error. Los flujos que cruzan fronteras entre subsistemas se unen en uno solo, sin perder ningún paso de las notas. Cada afirmación conserva la cita que traen las notas.
   - **Presupuesto de herramientas de la consolidación:** esta fase parte de las notas, pero no tiene tope de consultas por diseño. Usa las herramientas siempre que haga falta para comprobar una cita dudosa (una nota que contradice a otra o a las facetas, o cuya cita no cuadra con lo que ves), cubrir un hueco concreto (una pregunta del informe que ninguna nota responde) o confirmar el camino de activación de un hallazgo grave. Lo que no hayas podido comprobar, márcalo como inferido.
   - Un subsistema sin notas (excluido o fallido) no se describe como analizado ni se completa por suposición.
   - Añade como último apartado, justo antes de la línea de metadatos, `## Cobertura`, con tres listas: subsistemas cubiertos (una línea con los flujos que aportan), excluidos con su motivo, y fallidos o incompletos con lo que quedó sin verse. Cada subsistema se nombra exactamente como figura en el resumen de cobertura. Una frase final indica que las conclusiones del informe valen para lo cubierto. Este apartado no define hallazgos ni lleva identificadores.
   - Si las notas llegan vacías y el resumen de cobertura también, no hubo preparación por subsistemas: trabaja como si estos dos bloques no existieran, con las facetas y las herramientas, y omite el apartado «Cobertura». Si solo trae contenido el resumen de cobertura, redacta el apartado únicamente con lo que dice.
6. Elabora el informe con diagramas Mermaid representativos y vistosos según las directrices establecidas.

---

{{ include '_provided_documentation.md' }}

**Identificadores de hallazgo de este informe:** el número de informe es `04`. Si el informe enumera riesgos o hallazgos con identificador, usan el formato `04-CAT-nnn` (por ejemplo `04-PRO-001`, `04-ERR-001`).

@[_common_rules.md]

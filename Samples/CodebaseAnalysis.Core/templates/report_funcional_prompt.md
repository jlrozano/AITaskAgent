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
1. Revisa las facetas pre-extraídas considerando el ecosistema del proyecto (`{{ detected_languages }}`).
2. Analiza detalladamente la misión funcional del software, sus casos de uso y los procesos de negocio a los que da soporte.
3. Examina detalladamente los flujos y modelos identificados, explicando qué hace cada componente, a qué usuarios o procesos sirve y cómo se orquesta la operativa. Para seguir un caso de uso desde su punto de entrada, usa `graph_domain` (`view: routes`) y `graph_neighbors` sobre la ruta y su clase (quién la atiende, a qué llama y qué pantalla muestra).
4. **Citas de código:** cada módulo, caso de uso, entidad o proceso que describas lleva su cita `ruta:línea` (el punto de entrada, la clase o la tabla de donde sale), tomada de los datos pre-extraídos o de lo que leas con las herramientas. Lo que no puedas citar se marca como inferido.
5. **Notas de investigación y apartado «Cobertura»** (solo si las notas o el resumen de cobertura traen contenido):
   - Las notas son evidencia de segunda mano. Úsalas como base del informe, contrástalas con las facetas y no des por confirmado lo que contradiga a otra nota o a las facetas. Si una nota contradice al código, manda el código y se dice. Lo que las notas marcan como inferido sigue como inferido. Intégralas en el análisis sin perder su contenido.
   - **Integración completa, sin límite de extensión:** cada nota describe un subsistema con todo lo que su investigación encontró. El informe integra **todo** ese contenido y no un resumen: cada regla de negocio, estado y transición, excepción, código de error, constante de negocio, entidad y cita de las notas aparecen en el apartado que les corresponde, reorganizados por tema y escritos una sola vez cuando dos notas dicen lo mismo. No hay límite de longitud: el informe mide lo que mida su contenido, y se prefiere uno largo y completo a uno corto que descarte reglas o datos. Cada capacidad de negocio se desarrolla en su propio apartado (qué hace, quién la usa, reglas de negocio, estados y transiciones, datos, flujos alternativos y excepciones, fronteras con otras capacidades), con el nivel de detalle de las notas. Las secciones transversales (misión, sector, arquitectura, actores, conclusión) se añaden a ese desarrollo y no lo sustituyen. Cada afirmación conserva la cita que traen las notas.
   - **Presupuesto de herramientas de la consolidación:** esta fase parte de las notas, pero no tiene tope de consultas por diseño. Usa las herramientas siempre que haga falta para comprobar una cita dudosa (una nota que contradice a otra o a las facetas, o cuya cita no cuadra con lo que ves), cubrir un hueco concreto (una pregunta del informe que ninguna nota responde) o confirmar el camino de activación de un hallazgo grave. Lo que no hayas podido comprobar, márcalo como inferido.
   - Un subsistema sin notas (excluido o fallido) no se describe como analizado ni se completa por suposición.
   - Añade como último apartado, justo antes de la línea de metadatos, `## Cobertura`, con tres listas: subsistemas cubiertos (una línea con lo que aportan), excluidos con su motivo, y fallidos o incompletos con lo que quedó sin verse. Cada subsistema se nombra exactamente como figura en el resumen de cobertura. Una frase final indica que las conclusiones del informe valen para lo cubierto. Este apartado no define hallazgos ni lleva identificadores.
   - Si las notas llegan vacías y el resumen de cobertura también, no hubo preparación por subsistemas: trabaja como si estos dos bloques no existieran, con las facetas y las herramientas, y omite el apartado «Cobertura». Si solo trae contenido el resumen de cobertura, redacta el apartado únicamente con lo que dice.
6. Redacta el informe completo en formato Markdown estructurado según las directrices establecidas.

---

{{ include '_provided_documentation.md' }}

**Identificadores de hallazgo de este informe:** el número de informe es `01`. Si el informe enumera elementos con identificador (capacidades, casos de uso), usan el formato `01-CAT-nnn` (por ejemplo `01-FUN-001`).

@[_common_rules.md]

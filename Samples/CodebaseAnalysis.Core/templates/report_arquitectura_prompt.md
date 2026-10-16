Genera el **Informe de Arquitectura, Acoplamiento y Dependencias Modulares** para el repositorio analizado, aplicando un criterio arquitectónico pragmático y ajustado a su ecosistema tecnológico.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Componentes, Proyectos o Módulos Raíz**:
```json
{{ project_components }}
```

- **Métricas de Acoplamiento e Inestabilidad de Martin**:
```json
{{ martin_instability_metrics }}
```

- **Ciclos de Dependencias Detectados (`in_cycle = true`)**:
```json
{{ dependency_cycles }}
```

- **Violaciones de Reglas de Capas (`layer_violation = true`)**:
```json
{{ layer_violations }}
```

- **Comunidades y Clústeres Modulares (`community_id`)**:
```json
{{ architectural_communities }}
```

- **Jerarquía de Tipos por Supertipo (`INHERITS`, `IMPLEMENTS`)**: los más extendidos o implementados primero; `resolved` son aristas resueltas por un enriquecedor y `heuristic_cbm` herencias decididas por nombre.
```json
{{ type_hierarchy }}
```

- **Pantallas (JSP) con Acceso a Datos (SQL, `java.sql`, DAO) o Código de Negocio en Scriptlets (`suspicious`, con sus motivos; `session_access` es un dato aparte y no marca la pantalla)**:
```json
{{ suspicious_jsps }}
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
1. **Análisis de Fronteras Modulares y Arquitectura Real**:
   - Ajusta tu análisis al stack tecnológico detectado: `{{ detected_languages }}`.
   - Si es un proyecto multi-proyecto/ensamblados (.NET, Java, etc.), audita las referencias entre proyectos y paquetes.
   - Si es una aplicación monolítica o de escritorio (VBA/Access, scripts, Python), audita la separación entre formularios/UI, módulos de lógica de negocio, módulos de conexión/acceso a datos y tablas/consultas en base de datos.
   - Evalúa si el núcleo de lógica de negocio está debidamente aislado frente a dependencias de bajo nivel (bases de datos, APIs externas, automatización COM).
   - Reconstruye las dependencias entre capas desde el grafo: la jerarquía pre-extraída, y `graph_neighbors` sobre las clases base, los puntos de entrada y las pantallas para ver quién llama a quién. Una pantalla sospechosa es una violación de capas solo si su código lo confirma.

2. **Diagnóstico de Ciclos y Violaciones de Capas**:
   - Analiza los ciclos y dependencias circulares identificados a partir de los datos semánticos pre-extraídos.
   - Determina si los ciclos son incidentales por métodos auxiliares o estructurales por mala asignación de responsabilidades.

3. **Métricas de Martin y Estabilidad**:
   - Interpreta los valores de Inestabilidad ($I$), Abstracción ($A$) y Distancia ($D$) para destacar componentes en la Zona de Dolor o Zona de Inutilidad según la naturaleza del lenguaje. Si el repositorio es un único proyecto o todos los acoplamientos (aferente y eferente) valen cero, las métricas no miden nada: una sola línea lo dice y no se calculan ni se interpretan.

4. **Notas de investigación y apartado «Cobertura»** (solo si las notas o el resumen de cobertura traen contenido):
   - Las notas describen capas, dependencias y acoplamiento por subsistema. Son evidencia de segunda mano: úsalas como base, cruza las dependencias entre subsistemas (lo que un subsistema declara como saliente debe aparecer como entrante en el otro), contrasta con las métricas pre-extraídas y no des por confirmada una dependencia que sustente una conclusión si contradice a las métricas o a otra nota. Si una nota contradice al código, manda el código y se dice. Lo que las notas marcan como inferido sigue como inferido. No las copies: intégralas.
   - **Integración completa, sin límite de extensión:** cada nota describe un subsistema con todo lo que su investigación encontró. El informe integra **todo** ese contenido y no un resumen: cada capa, dependencia entrante y saliente, acoplamiento, ciclo, punto de integración y cita de las notas aparecen en el apartado que les corresponde, reorganizados por tema y escritos una sola vez cuando dos notas dicen lo mismo. No hay límite de longitud: el informe mide lo que mida su contenido, y se prefiere uno largo y completo a uno corto que descarte reglas o datos. Cada subsistema se desarrolla con sus capas y sus dependencias en ambos sentidos; las dependencias entre subsistemas se cruzan y se dejan completas, sin perder ninguna de las notas. Cada afirmación conserva la cita que traen las notas.
   - **Presupuesto de herramientas de la consolidación:** esta fase parte de las notas, pero no tiene tope de consultas por diseño. Usa las herramientas siempre que haga falta para comprobar una cita dudosa (una nota que contradice a otra o a las facetas, o cuya cita no cuadra con lo que ves), cubrir un hueco concreto (una pregunta del informe que ninguna nota responde) o confirmar el camino de activación de un hallazgo grave. Lo que no hayas podido comprobar, márcalo como inferido.
   - Un subsistema sin notas (excluido o fallido) no se describe como analizado ni se completa por suposición. Las dependencias hacia o desde él que aparezcan en otras notas se citan como tales.
   - Añade como último apartado, justo antes de la línea de metadatos de las directrices, `## Cobertura`, con tres listas: subsistemas cubiertos (una línea con lo que aportan), excluidos con su motivo, y fallidos o incompletos con lo que quedó sin verse. Cada subsistema se nombra exactamente como figura en el resumen de cobertura. Una frase final indica que las conclusiones del informe valen para lo cubierto. Este apartado no define hallazgos ni lleva identificadores.
   - Si las notas llegan vacías y el resumen de cobertura también, no hubo preparación por subsistemas: trabaja como si estos dos bloques no existieran, con las facetas y las herramientas, y omite el apartado «Cobertura». Si solo trae contenido el resumen de cobertura, redacta el apartado únicamente con lo que dice.

5. **Elaboración del Informe y Soluciones en 3 Niveles**:
   - Incluye diagramas Mermaid de la topología actual y de la arquitectura modular recomendada.
   - Formula una hoja de ruta con hitos claros en 3 niveles (Nivel 1: Arquitectura/Módulos, Nivel 2: Diseño/Composición, Nivel 3: Implementación).

---

{{ include '_provided_documentation.md' }}

**Identificadores de hallazgo de este informe:** el número de informe es `06`. Todos llevan el formato `06-CAT-nnn` (por ejemplo `06-ARQ-001`).

@[_common_rules.md]

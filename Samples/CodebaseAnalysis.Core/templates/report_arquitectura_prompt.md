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

- **Pantallas (JSP) con Código de Negocio, SQL o Sesión (`suspicious`, con sus motivos)**:
```json
{{ suspicious_jsps }}
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
   - Interpreta los valores de Inestabilidad ($I$), Abstracción ($A$) y Distancia ($D$) para destacar componentes en la Zona de Dolor o Zona de Inutilidad según la naturaleza del lenguaje.

4. **Elaboración del Informe y Soluciones en 3 Niveles**:
   - Incluye diagramas Mermaid de la topología actual y de la arquitectura modular recomendada.
   - Formula una hoja de ruta con hitos claros en 3 niveles (Nivel 1: Arquitectura/Módulos, Nivel 2: Diseño/Composición, Nivel 3: Implementación).

---

{{ include '_provided_documentation.md' }}

@[_common_rules.md]

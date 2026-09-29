### Directrices de Diagramación Mermaid de Alta Vistosidad (Obligatorio):

1. **Tipos de Diagramas Soportados**:
   - Utiliza únicamente: `flowchart TD`, `flowchart LR`, `sequenceDiagram`, `classDiagram`, `erDiagram` o `stateDiagram-v2`.
   - **PROHIBIDO** utilizar diagramas experimentales o no compatibles como `mindmap`, `quadrantChart` o `pie`.

2. **Reglas de Sintaxis Estricta** (un diagrama que no compila no se ve; revisa cada una antes de emitirlo):
   - **Identificadores seguros:** el id de un nodo, subgrafo, participante, estado o entidad es solo letras ASCII, dígitos y `_`, empieza por letra y no lleva espacios, tildes, puntos ni guiones: `Pedido_Filtro`, `n1`, `svcExport`. El texto visible va en la etiqueta, nunca en el id.
   - **Nunca uses como id una palabra reservada**, en ninguna combinación de mayúsculas: `end`, `graph`, `flowchart`, `subgraph`, `direction`, `style`, `classDef`, `class`, `click`, `linkStyle`, `default`, `call`, `href`, `state`, `note`, `participant`, `actor`, `as`, `loop`, `alt`, `else`, `opt`, `par`, `and`, `rect`, `critical`, `break`, `activate`, `deactivate`, `autonumber`, `off`, `on`, `left`, `right`, `over`, `of`, `box`, `create`, `destroy`, `namespace`. Si el nombre natural coincide con una (un estado `OFF`, un módulo `End`), pon un prefijo al id y deja el nombre en la etiqueta: `st_off["OFF"]`, `participant m_end as "End"`.
   - **Etiquetas:** si el texto contiene cualquier carácter que no sea letra, dígito o espacio, enciérralo entre comillas dobles: `NodoId["Texto (Detalle adicional)"]`. Dentro de una etiqueta entre comillas **nunca** aparece otra comilla doble: ni duplicada (`[""x""]`), ni escapada con barra (`\"`), que Mermaid no admite. Sustitúyela por comilla simple (`'x'`) o por `#quot;`. Tampoco van dentro de la etiqueta `;`, `|` ni saltos de línea: usa `#59;`, `#124;` o reformula.
   - **Textos de aristas:** `A -->|texto| B`; si el texto lleva puntuación, `A -->|"texto, con puntuación"| B`, con las mismas reglas de comillas.
   - **`sequenceDiagram`:** declara cada participante con id seguro y alias, `participant FRM as "FFiltro (formulario)"`, y usa siempre ese id en los mensajes. El texto de un mensaje va tras `:` y no lleva `;`.
   - **`stateDiagram-v2`:** estados con id seguro y descripción aparte, `state "Sin conexión" as st_offline`; el inicio y el fin son `[*]`.
   - **`erDiagram`:** entidades con id seguro (sin espacios ni puntos), atributos `tipo nombre` sin comillas, y la etiqueta de la relación entre comillas: `CLIENTE ||--o{ PEDIDO : "realiza"`.
   - **`classDiagram`:** clases con id seguro; los genéricos se escriben con `~T~`, no con `<T>`.
   - Cada `subgraph` se cierra con su `end` y tiene un id distinto de los de sus nodos: `subgraph sg_datos["Capa de datos"]`.
   - **PROHIBIDO** incluir etiquetas HTML (`<br/>`, `<b>`, etc.) dentro de los textos de los nodos.

3. **Paleta de Colores y Estilos Estándar**:
   - Aplica estilos explícitos mediante directivas `style NodeId fill:#hex,color:#fff,stroke:#hex`:
     - 🔵 **Interfaces / Abstracciones / APIs / Rutas**: `fill:#2980b9,color:#fff,stroke:#1a5276`
     - 🟢 **Servicios / Lógica de Negocio / Implementaciones Limpias**: `fill:#27ae60,color:#fff,stroke:#1e8449`
     - ⚫ **Bases de Datos / Persistencia / Recursos de Almacenamiento**: `fill:#2c3e50,color:#fff,stroke:#1a252f`
     - 🟣 **Contenedor / Módulos de Factoría / Orquestadores**: `fill:#5b2d8e,color:#fff,stroke:#3d1a63`
     - 🟠 **Puntos Críticos / Acoplamiento Directo / Riesgo Medio**: `fill:#e67e22,color:#fff,stroke:#ca6f1e`
     - 🔴 **Errores / Vulnerabilidades / Código Residual / Riesgo Alto**: `fill:#c0392b,color:#fff,stroke:#922b21`

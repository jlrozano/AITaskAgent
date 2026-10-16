Investiga un subsistema del repositorio para el **Informe de Arquitectura, Acoplamiento y Dependencias Modulares**. Tu salida son **notas de investigación** en texto plano, con citas de código, que otro auditor consolidará junto con las de los demás subsistemas. No redactes el informe ni su resumen ejecutivo, ni dibujes diagramas, ni propongas la arquitectura objetivo: documenta con evidencia cómo está construido este subsistema y de qué depende.

### Contexto del Repositorio:
{{ repository_context }}

### Subsistema a Investigar:
- **Nombre**: `{{ subsystem_name }}`
- **Resumen**: {{ subsystem_summary }}

- **Miembros del subsistema** (ficheros y tipos que lo componen; fuera de esta lista solo entras para seguir una dependencia):
```text
{{ subsystem_members }}
```

- **Puntos de entrada del subsistema** (rutas, acciones, pantallas, comandos o tipos públicos por los que el resto del sistema lo usa):
```text
{{ subsystem_entry_points }}
```

### Preguntas de la Investigación:
Responde cada pregunta con evidencia del código. Si una pregunta no se puede responder con lo que hay, dilo en «No comprobado» con el motivo.

{{ investigation_questions }}

### Evidencia Mínima Exigida:
{{ minimum_evidence }}

---

### Instrucciones de Recorrido:
1. **Identifica las capas reales del subsistema.** Clasifica sus miembros por responsabilidad (presentación o entrada, orquestación o servicios, lógica de dominio, acceso a datos, integración externa) con la evidencia que lo justifica: la clase base, el contrato que implementa, el recurso que usa. Un nombre de paquete o de clase orienta pero no prueba la capa.
2. **Mide las dependencias con el grafo.** Con `graph_neighbors` obtén quién llama a los miembros del subsistema (dependencias entrantes) y a quién llaman (salientes), con las líneas de cada llamada. En proyectos .NET usa además `graph_domain` con `view: projects` (dependencias entre proyectos frente a los `ProjectReference`) y `view: contracts` (contratos frente a implementaciones y consumidores). Usa `graph_search` para localizar nodos por nombre.
3. **Una dependencia se prueba con la línea de la llamada o del uso**, no con una importación o una referencia de proyecto. Confirma en el código (`view_code_item` por nombre, `view_file` por fichero) las que sustentan una conclusión, y trata las aristas heurísticas como pistas.
4. **Examina los puntos donde se decide la arquitectura:**
   - **Saltos de capa:** presentación o entrada que accede a datos o contiene lógica de negocio, lógica que construye consultas o conexiones, acceso a recursos externos fuera de la capa que le corresponde.
   - **Acoplamiento:** miembros con muchas dependencias entrantes o salientes, dependencias hacia detalles concretos en lugar de contratos, tipos que conocen la estructura interna de otro subsistema, datos compartidos con otros subsistemas (tablas, ficheros, estado global).
   - **Ciclos:** dependencias circulares entre miembros del subsistema o con otros subsistemas, y si son incidentales o estructurales.
   - **Jerarquías y contratos:** herencias profundas o anchas, interfaces con una sola implementación y las que no tienen ninguna, puntos de extensión.
   - **Composición:** dónde se crean y se conectan los colaboradores (constructores, contenedor, factorías, instanciación directa).
5. **Busca con `grep_search` lo que el grafo no ve**: clases referenciadas por nombre en configuración XML o en cadenas, instanciación por reflexión, acceso directo a recursos desde pantallas. Si la búsqueda llega al tope de líneas, acótala con `Includes` en lugar de suponer que no hay más.
6. **Busca un contraejemplo.** Encuentra al menos un caso en el que una frontera se respeta, o una dependencia que parece una violación de capas y no lo es (un acceso justificado por el propio diseño, una llamada que atraviesa un contrato), y descríbelo con su cita. Si tras buscar no hay ninguno, dilo y cita lo que revisaste.
7. **Respeta la frontera del subsistema.** Las dependencias hacia otros subsistemas se anotan con su cita y su dirección; el interior de esos subsistemas se deja a su propia investigación.
8. **Si te quedas sin presupuesto de herramientas**, redacta con lo consultado y declara en «No comprobado» lo que quedó sin ver.

### Formato de Salida (texto plano):
- Entrega **texto plano**: sin JSON, sin etiquetas XML o HTML, sin bloques de código, sin encabezados Markdown, sin diagramas y sin línea `analysis-meta`.
- La primera línea es `Subsistema: {{ subsystem_name }}`. Después, bloques cuyo título es una línea que termina en dos puntos, en este orden: `Capas y responsabilidades:`, `Dependencias entrantes:`, `Dependencias salientes:`, `Acoplamiento, ciclos y saltos de capa:`, `Contratos, jerarquías y composición:`, `Contraejemplo:` y `No comprobado:`. Un bloque sin contenido propio se reduce a una línea («Sin hallazgos con la evidencia disponible»).
- Cada afirmación sobre el código termina con su cita en formato de código, `ruta:línea` o `ruta:inicio-fin` (`src/Orders/OrderRepository.cs:42`), con la ruta relativa al repositorio. Una dependencia se cita con la línea del uso, y se indica su dirección (quién depende de quién).
- Lo que no puedas citar, o una dependencia que solo sostenga una arista heurística, se escribe con el prefijo `(inferido)` y el motivo. Un dato vacío en el grafo no prueba que una dependencia no exista: lo no verificado va a «No comprobado», no a una afirmación de ausencia.
- Prosa técnica impersonal y afirmativa, anclada a datos (nombres, cifras de acoplamiento, ficheros, líneas). Sin conocimiento general, sin relleno, sin hoja de ruta ni recomendaciones, sin identificadores de hallazgo, sin coletillas de chat y sin ofrecer más ayuda: la última línea es la última nota.
- No reproduzcas nunca credenciales ni secretos que encuentres; di solo dónde están.

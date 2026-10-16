Investiga un subsistema del repositorio para el **Informe de Flujos End-to-End y Procesos de Negocio**. Tu salida son **notas de investigación** en texto plano, con citas de código, que otro auditor consolidará junto con las de los demás subsistemas. No redactes el informe ni su resumen ejecutivo, ni dibujes diagramas: reconstruye con evidencia los caminos de ejecución de este subsistema.

### Contexto del Repositorio:
{{ repository_context }}

### Subsistema a Investigar:
- **Nombre**: `{{ subsystem_name }}`
- **Resumen**: {{ subsystem_summary }}

- **Miembros del subsistema** (ficheros y tipos que lo componen; fuera de esta lista solo entras para seguir una llamada):
```text
{{ subsystem_members }}
```

- **Puntos de entrada del subsistema** (rutas, acciones, pantallas, eventos, triggers, comandos o métodos públicos por los que se inicia un flujo):
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
1. **Un flujo por punto de entrada relevante, de principio a fin.** Reconstruye el camino completo desde la entrada (petición, evento de pantalla, trigger, comando, tarea temporizada) hasta la operación terminal (escritura en base de datos, transacción, fichero generado, mensaje enviado, respuesta o pantalla). Un flujo cortado a medio camino no cuenta: si no llegas a la operación terminal, di dónde te detuviste y por qué.
2. **Sigue las aristas con las herramientas.** Localiza la entrada con `graph_domain` (`view: routes`) y `graph_search`. Con `graph_neighbors` avanza desde la pantalla a la acción a la que envía (`SUBMITS_TO`), desde la acción a la clase que la atiende (`HANDLES`) y a las pantallas a las que reenvía (`FORWARDS_TO`), y desde el método de entrada a sus llamadas (`CALLS`, con las líneas). Usa las aristas resueltas como camino y confirma las heurísticas en el código.
3. **Lee cada paso.** Cada paso se confirma leyendo el método (`view_code_item` por nombre, `view_file` por fichero). Anota en cada paso qué datos viajan (parámetros, objetos de formulario, DTO, tablas o columnas) y dónde se toman decisiones (la condición y sus ramas).
4. **Registra lo que condiciona el flujo**: dónde empieza y termina la transacción, qué pasos son síncronos y cuáles diferidos, qué se valida y dónde, qué se transforma y qué se persiste.
5. **Recorre los caminos alternativos de cada flujo**: validación fallida, entrada vacía o duplicada, falta de permisos, error en un paso intermedio, cancelación, reintento. Para cada uno, anota adónde termina el flujo (pantalla de error, reenvío, excepción propagada, error silenciado).
6. **Busca con `grep_search` lo que el grafo no ve**: reenvíos y rutas escritos en configuración XML o en cadenas, nombres de clase instanciados por reflexión, eventos registrados por nombre. Si la búsqueda llega al tope de líneas, acótala con `Includes` en lugar de suponer que no hay más. Revisa también los reenvíos sin destino resuelto que afecten al subsistema.
7. **Respeta la frontera del subsistema.** Cuando un flujo sale hacia otro subsistema o hacia un sistema externo, anota la frontera con la cita de la llamada y los datos que cruzan, y no sigas dentro: de eso se encarga la investigación de ese otro subsistema.
8. **Busca un contraejemplo.** Encuentra al menos un caso en el que el camino que sugiere el grafo o el nombre de una clase no coincide con el código (una arista heurística que el código no confirma, un flujo que termina antes de persistir, una entrada que acaba en un destino distinto al esperado) y descríbelo con su cita. Si tras buscar no hay ninguno, dilo y cita lo que revisaste.
9. **Si te quedas sin presupuesto de herramientas**, redacta con lo consultado y declara en «No comprobado» lo que quedó sin ver.

### Formato de Salida (texto plano):
- Entrega **texto plano**: sin JSON, sin etiquetas XML o HTML, sin bloques de código, sin encabezados Markdown, sin diagramas y sin línea `analysis-meta`.
- La primera línea es `Subsistema: {{ subsystem_name }}`. Después, bloques cuyo título es una línea que termina en dos puntos, en este orden: `Flujos principales:` (cada flujo como una secuencia numerada de pasos, desde la entrada hasta la operación terminal), `Datos que viajan y decisiones:`, `Transacciones y operaciones terminales:`, `Caminos alternativos y contraejemplo:`, `Frontera con otros subsistemas:` y `No comprobado:`. Un bloque sin contenido propio se reduce a una línea («Sin hallazgos con la evidencia disponible»).
- Cada paso de un flujo, cada decisión y cada operación terminal terminan con su cita en formato de código, `ruta:línea` o `ruta:inicio-fin` (`src/Orders/OrderRepository.cs:42`), con la ruta relativa al repositorio.
- Lo que no puedas citar, o un paso reconstruido solo por heurística, se escribe con el prefijo `(inferido)` y el motivo. Un dato vacío en el grafo no prueba que un camino no exista: lo no verificado va a «No comprobado», no a una afirmación de ausencia.
- Prosa técnica impersonal y afirmativa, anclada a datos (nombres, valores, ficheros, líneas). Sin conocimiento general, sin relleno, sin valoraciones ni recomendaciones, sin identificadores de hallazgo, sin coletillas de chat y sin ofrecer más ayuda: la última línea es la última nota.
- No reproduzcas nunca credenciales ni secretos que encuentres; di solo dónde están.

Investiga un subsistema del repositorio para el **Informe de Errores, Bugs y Vulnerabilidades con Criticidad y Soluciones**. Tu salida son **notas de investigación** en texto plano, con citas de código, que otro auditor consolidará junto con las de los demás subsistemas. No redactes el informe, no uses su matriz de criticidad ni asignes severidades: documenta con evidencia los defectos candidatos de este subsistema, cómo fallarían y qué los condiciona.

### Contexto del Repositorio:
{{ repository_context }}

### Subsistema a Investigar:
- **Nombre**: `{{ subsystem_name }}`
- **Resumen**: {{ subsystem_summary }}

- **Miembros del subsistema** (ficheros y tipos que lo componen; fuera de esta lista solo entras para seguir una llamada):
```text
{{ subsystem_members }}
```

- **Puntos de entrada del subsistema** (rutas, acciones, pantallas, comandos o métodos públicos por los que llega entrada externa al subsistema):
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
1. **Empieza por la entrada externa.** Con `graph_domain` (`view: routes`) y `graph_neighbors` sigue cada punto de entrada hasta donde su dato llega a una consulta, un comando, un fichero, una deserialización, una pantalla o una llamada externa. Allí está la superficie de los defectos de entrada.
2. **Prioriza el código con más riesgo.** Dentro del subsistema, lee primero los métodos que acceden a datos o a recursos, los que construyen consultas o comandos, los que manejan excepciones y los de mayor complejidad. Usa `graph_search` para localizarlos y `view_code_item` (por nombre) o `view_file` (por fichero) para leerlos.
3. **Un defecto candidato exige leer el código.** El grafo dice dónde mirar; el hallazgo sale de la línea. Para cada candidato anota la vía de fallo (qué entrada o qué condición lo dispara), el efecto observable (dato perdido o corrupto, recurso retenido, error oculto, acceso indebido) y qué condición adicional hace falta, si hace falta alguna. No asignes severidad.
4. **Cubre estas familias de defectos** según el lenguaje y la plataforma del repositorio (están en el contexto):
   - **Caminos de error:** excepciones capturadas y descartadas, capturas demasiado amplias, errores de retorno ignorados, fallos que se convierten en un valor por defecto, reintentos sin límite, estado a medias tras un fallo, transacciones sin retroceso.
   - **Recursos:** conexiones, cursores, ficheros, transmisiones y objetos externos que se abren sin garantía de cierre en todos los caminos, también en los de error.
   - **Entrada y seguridad:** consultas o comandos construidos por concatenación, deserialización o evaluación de entrada externa, rutas sin autenticación o autorización donde el código sí gestiona datos sensibles, credenciales escritas en el código o en configuración versionada.
   - **Concurrencia y asincronía:** estado compartido mutable, bloqueos sobre llamadas asíncronas, tareas que nadie espera, condiciones de carrera.
   - **Datos:** referencias nulas sin comprobar, conversiones de tipo implícitas, límites y valores vacíos que no se contemplan.
5. **Busca con `grep_search` lo que el grafo no ve**: patrones de captura vacía, cierres de recursos, construcción de consultas y literales de credenciales, también en configuración XML y en pantallas. Si la búsqueda llega al tope de líneas, acótala con `Includes` en lugar de suponer que no hay más.
6. **Busca un contraejemplo.** Descarta al menos un candidato que parecía defecto y no lo es (una captura que sí registra y propaga, un recurso que sí se cierra en todos los caminos, una consulta parametrizada, una ruta que valida por una política global) y explica con su cita por qué se descarta. Una entrada de credenciales que es claramente un falso positivo (referencia a un secreto, identificador, marcador de posición) se descarta con su fichero, su línea y el motivo. Ante la duda, un secreto se trata como real.
7. **Respeta la frontera del subsistema.** Un defecto cuya causa está en otro subsistema se anota con la cita de la llamada y no se investiga allí.
8. **Si te quedas sin presupuesto de herramientas**, redacta con lo consultado y declara en «No comprobado» lo que quedó sin ver.

### Formato de Salida (texto plano):
- Entrega **texto plano**: sin JSON, sin etiquetas XML o HTML, sin bloques de código, sin encabezados Markdown, sin diagramas y sin línea `analysis-meta`.
- La primera línea es `Subsistema: {{ subsystem_name }}`. Después, bloques cuyo título es una línea que termina en dos puntos, en este orden: `Defectos candidatos:` (cada uno con su cita, su vía de fallo, su efecto y la condición que lo hace explotable o visible), `Caminos de error y recursos:`, `Entrada externa y seguridad:`, `Candidatos descartados (contraejemplo):`, `Frontera con otros subsistemas:` y `No comprobado:`. Un bloque sin contenido propio se reduce a una línea («Sin hallazgos con la evidencia disponible»).
- Cada afirmación sobre el código termina con su cita en formato de código, `ruta:línea` o `ruta:inicio-fin` (`src/Orders/OrderRepository.cs:42`), con la ruta relativa al repositorio.
- Lo que no puedas citar, o un defecto que no hayas confirmado leyendo la línea, se escribe con el prefijo `(inferido)` y el motivo. Un dato vacío en el grafo no prueba que un defecto no exista: lo no verificado va a «No comprobado», no a una afirmación de ausencia.
- Prosa técnica impersonal y afirmativa, anclada a datos (nombres, valores, ficheros, líneas). Sin conocimiento general, sin relleno, sin recomendaciones de solución, sin identificadores de hallazgo, sin coletillas de chat y sin ofrecer más ayuda: la última línea es la última nota.
- **Nunca reproduzcas el valor de una credencial**, ni siquiera enmascarado: di solo el fichero, la línea y qué recurso protege.

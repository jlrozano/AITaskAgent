Investiga un subsistema del repositorio para el **Informe de Análisis Funcional: ¿Qué hace la aplicación?**. Tu salida son **notas de investigación** en texto plano, con citas de código, que otro auditor consolidará junto con las de los demás subsistemas. No redactes el informe ni su resumen ejecutivo, ni dibujes diagramas: documenta con evidencia qué hace este subsistema.

### Contexto del Repositorio:
{{ repository_context }}

### Subsistema a Investigar:
- **Nombre**: `{{ subsystem_name }}`
- **Resumen**: {{ subsystem_summary }}

- **Miembros del subsistema** (ficheros y tipos que lo componen; fuera de esta lista solo entras para seguir una llamada):
```text
{{ subsystem_members }}
```

- **Puntos de entrada del subsistema** (rutas, acciones, pantallas, comandos o métodos públicos por los que se accede a él):
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
1. **Parte de los puntos de entrada.** Para cada uno, localiza la ruta o la acción con `graph_domain` (`view: routes`) y sigue el caso de uso con `graph_neighbors` sobre la ruta y su clase: quién la atiende, a qué llama y qué pantalla muestra. Usa `graph_search` para localizar un nodo por su nombre.
2. **Lee el código antes de afirmar.** El grafo dice dónde mirar; la afirmación sale de leer el método (`view_code_item` por nombre, `view_file` por fichero). Una arista heurística es una pista y se confirma en el código.
3. **Reconstruye lo que es funcional**, no lo estructural:
   - **Casos de uso:** qué operación de negocio dispara cada entrada y qué resultado produce.
   - **Reglas de negocio:** condiciones, validaciones, cálculos, umbrales y prioridades, con qué ocurre cuando no se cumplen.
   - **Estados y transiciones:** campos, enumeraciones o tablas que modelan un estado, sus valores, quién los cambia y desde qué estado a cuál. Anota los estados que nadie alcanza o que ninguna regla consulta.
   - **Actores:** usuarios, roles, procesos temporizados o por lotes y sistemas externos que disparan el caso de uso o consumen su resultado.
   - **Datos:** entidades, tablas y ficheros que se crean, leen, modifican o borran, y con qué clave.
4. **Busca con `grep_search` lo que el grafo no ve**: literales de estado, nombres de clase en cadenas, configuración XML, factorías que instancian por reflexión. Si la búsqueda llega al tope de líneas, acótala con `Includes` en lugar de suponer que no hay más.
5. **Respeta la frontera del subsistema.** Cuando un flujo sale hacia otro subsistema o hacia un sistema externo, anota la frontera con la cita de la llamada y no sigas dentro: de eso se encarga la investigación de ese otro subsistema.
6. **Busca un contraejemplo.** Encuentra al menos un caso en el que lo que sugiere un nombre, una regla general o una entrada no se cumple (una rama que se salta la regla, un estado que no sigue el ciclo habitual, un caso de uso que no hace lo que su nombre promete) y descríbelo con su cita. Si tras buscar no hay ninguno, dilo y cita lo que revisaste.
7. **Si te quedas sin presupuesto de herramientas**, redacta con lo consultado y declara en «No comprobado» lo que quedó sin ver.

### Formato de Salida (texto plano):
- Entrega **texto plano**: sin JSON, sin etiquetas XML o HTML, sin bloques de código, sin encabezados Markdown, sin diagramas y sin línea `analysis-meta`.
- La primera línea es `Subsistema: {{ subsystem_name }}`. Después, bloques cuyo título es una línea que termina en dos puntos, en este orden: `Qué hace:`, `Entradas y actores:`, `Reglas de negocio:`, `Estados y transiciones:`, `Datos y entidades:`, `Caminos alternativos y contraejemplo:`, `Frontera con otros subsistemas:` y `No comprobado:`. Un bloque sin contenido propio se reduce a una línea («Sin hallazgos con la evidencia disponible»).
- Dentro de cada bloque, párrafos breves o líneas que empiezan por un guion. Cada afirmación sobre el código termina con su cita en formato de código, `ruta:línea` o `ruta:inicio-fin` (`src/Orders/OrderRepository.cs:42`), con la ruta relativa al repositorio.
- Lo que no puedas citar se escribe con el prefijo `(inferido)` y el motivo de la inferencia. Un dato vacío en el grafo no prueba que algo no exista: lo no verificado va a «No comprobado», no a una afirmación de ausencia.
- Prosa técnica impersonal y afirmativa, anclada a datos (nombres, valores, ficheros, líneas). Sin conocimiento general, sin relleno, sin valoraciones ni recomendaciones, sin identificadores de hallazgo, sin coletillas de chat y sin ofrecer más ayuda: la última línea es la última nota.
- No reproduzcas nunca credenciales ni secretos que encuentres; di solo dónde están.

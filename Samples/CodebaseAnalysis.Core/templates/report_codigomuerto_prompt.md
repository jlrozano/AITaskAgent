Genera el **Informe de Detección de Código Muerto y Elementos Huérfanos** para el repositorio analizado.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Totales de Candidatos a Código Muerto**: lo que marca el análisis de alcanzabilidad del compilador (Roslyn en C#, javac en Java), por confianza y por qué no se listan. En un repositorio grande son miles: da estos totales en el informe y analiza los candidatos que siguen.
```json
{{ dead_code_summary }}
```

- **Candidatos a Código Muerto por Alcanzabilidad (`dead_code_candidate = true`)**: sin duplicados (si una clase es candidata no se listan sus métodos: `dead_members` dice cuántos), sin los métodos privados sin uso (van aparte) ni los que sobrescriben o implementan un contrato (`OVERRIDES`), ordenados por confianza (`high`, `medium`, `low`) y después tipos antes que miembros y por tamaño. Son candidatos verificables contra el código, no pruebas: una confianza `low` o `medium` suele ser API pública, reflexión o un contrato.
```json
{{ dead_code_candidates }}
```

- **Métodos Privados sin Uso** (confianza alta): métodos `private` de clases vivas que ningún código vivo referencia; las referencias son exactas del compilador, solo la reflexión podría llamarlos. Son los candidatos más fiables y los más baratos de borrar; confirma cada uno con su cita `ruta:línea`.
```json
{{ dead_private_methods }}
```

- **Registros o Factorías no Referenciadas**: registros de DI sin consumidor. `consumer_count = 0` lo calcula el compilador (los miembros que piden el tipo del servicio, sin contar el registro); `consumed_by_framework` lo resuelve el host. Es un candidato: una resolución por reflexión o por `IServiceProvider` no la ve.
```json
{{ unused_di_registrations }}
```

- **Rutas, Consultas o Puntos de Entrada Huérfanos**:
```json
{{ orphan_routes }}
```

- **Acciones Struts sin Clase que las Atienda (sin `HANDLES`, con el motivo)**:
```json
{{ struts_actions_without_handler }}
```

- **Clases, Tablas o Módulos con Cero Uso Entrante Conocido (`fan_in = 0`)**: solo los que tienen `fan_in` conocido, como propiedad del grafo o calculado de las aristas resueltas (`fan_in_source` dice cuáles). Los que no tienen el dato no aparecen: su uso entrante es desconocido, no cero. Quedan fuera los que atienden una ruta (`HANDLES`), los que tienen subtipos y los form-bean y acciones que la configuración de Struts instancia por reflexión. El `fan_in` calculado cuenta las llamadas a métodos y constructores del tipo (`CALLS`) y, si las hay, sus usos como tipo desde otros tipos (`USES_TYPE`: parámetro, retorno, variable, cast, `new` también con el constructor implícito, `X.CONSTANTE`…). Sin `USES_TYPE` en el grafo solo se usa en los tipos que declaran constructor, porque instanciar con el implícito no deja `CALLS`. No ve otra reflexión ni el uso desde JSP o XML.
```json
{{ zero_fanin_classes }}
```

- **Campos privados que nada lee (sin `READS`)**: campos `private` de C# y de Java que no son `static final` ni `const`, con su fichero y línea; `writes` cuenta los métodos que los escriben. Son candidatos: la reflexión, la serialización y los frameworks que leen o escriben campos por nombre (los `ActionForm` de Struts, los beans) no dejan `READS`.
```json
{{ unread_fields }}
```

- **Recuento de campos sin leer**: `never_touched` son los que nadie lee ni escribe (campo sin uso) y `write_only` los que se escriben y nunca se leen.
```json
{{ unread_fields_summary }}
```

- **Campos solo escritos (`writes > 0`, sin `READS`)**: estado que se guarda y nadie consulta, p. ej. un colaborador inyectado que se asigna en el constructor y no se usa. Distínguelos de los campos sin ningún acceso.
```json
{{ write_only_fields }}
```

- **Candidatos agrupados por un patrón común** (tipos sin uso y campos sin leer): los de las dos listas anteriores que comparten prefijo o sufijo del nombre (los campos, por el tipo que los declara) o directorio van aquí resumidos, con su patrón, su recuento y tres ejemplos, y no se repiten en las listas. `likely_dynamic` marca los grupos cuyo patrón aparece en un punto de instanciación por nombre o con miembros que nombra la configuración, con su `evidence`. Es una heurística: indica dónde mirar.
```json
{{ zero_fanin_groups }}
```
```json
{{ unread_field_groups }}
```

- **Instanciación dinámica y clases nombradas en configuración** (heurística por expresiones regulares): puntos donde el código crea o carga un tipo por nombre con un argumento que no es un literal, y cuántas clases nombra cada XML de configuración. Un candidato suelto que nombra la configuración o un literal del código lleva `named_in_configuration`.
```json
{{ dynamic_instantiation_sites }}
```

- **Nombres de clase que el compilador resolvió en la carga por reflexión (`reflection_targets`)**: el argumento de `Class.forName`, `loadClass`, `Type.GetType` o `Activator.CreateInstance` cuando es constante, o constante a trozos (`com.example.export.*Impl`, con «*» por cada tramo que llega de fuera). Las clases que ese patrón puede nombrar figuran en `kept_alive` y no son código muerto: no las presentes como candidatas a retirar, y cita el método que hace la llamada (`cite`).
```json
{{ reflection_targets }}
```

### Preparación del Repositorio (inventario, mapa de dominios y plan de cobertura):
Contexto para repartir el análisis por todo el repositorio. Los tres bloques llegan vacíos (`[]`, `{}` o sin texto) cuando no hubo preparación.

- **Inventario del Repositorio** (tamaño, lenguajes, cobertura del grafo frente al disco y límites de los datos):
```json
{{ repository_inventory }}
```

- **Mapa de Dominios** (subsistemas detectados, con su resumen y los ficheros que los componen):
```json
{{ domain_map }}
```

- **Plan de Cobertura** (subsistemas elegidos por prioridad, y excluidos con su motivo):
```json
{{ coverage_plan }}
```

---

### Instrucciones de Ejecución:
1. Revisa las listas de candidatos anteriores considerando el ecosistema del proyecto (`{{ detected_languages }}`).
   - En Access/VBA: formularios huérfanos, tablas temporales/backup (`_temp`, `_tmp`, `_bak`, `_old`, `_copy`, `_test`), consultas no vinculadas a ningún formulario o módulo.
   - En C#/.NET u otros: clases sin uso, endpoints sin handler, dependencias no resueltas.
2. Antes de declarar muerto un candidato, sigue la regla de puntos ciegos del grafo: busca quién lo llama o lo hereda con `graph_neighbors` (`direction: in`), si atiende una ruta (`HANDLES`) y si lo importa alguna JSP (`page_imports`), y comprueba la configuración que pueda referenciarlo. Lo que no puedas descartar queda como candidato sin confirmar, no como código muerto.
3. Un grupo grande de candidatos con un patrón común es señal de uso dinámico. Antes de afirmar que sus miembros están muertos, busca la factoría con `grep_search` (el sufijo, el prefijo o el paquete del grupo, en el código y en `**/*.xml`), lee con `view_file` el código que compone el nombre y comprueba que puede llegar a esas clases. Si la instanciación dinámica es plausible, lleva `likely_dynamic` o no, clasifica el grupo entero como **dudoso por instanciación dinámica**, con la evidencia (fichero y línea de la factoría o de la configuración), y no como código muerto; si no encuentras ninguna vía, dilo y déjalo como candidato sin confirmar. Un candidato con `named_in_configuration` no está muerto: lo nombra la configuración o el código.
4. **Protocolo de Cobertura por Subsistemas** (solo si el plan de cobertura trae subsistemas elegidos):
   - Recorre uno a uno los subsistemas elegidos del plan y examina los candidatos de cada uno (clases, métodos privados, rutas, registros), con la comprobación de puntos ciegos y de uso dinámico de los puntos 2 y 3. No concentres el informe en el subsistema que tenga más candidatos: un listado dominado por uno solo no representa el repositorio.
   - Cada subsistema elegido recibe como mínimo una comprobación con herramientas, con su cita `ruta:línea`, o la línea «Sin hallazgos con la evidencia disponible» si lo comprobado está limpio. Lo que no llegues a recorrer se declara como no cubierto, no se rellena por suposición.
   - Añade como último apartado, justo antes de la línea de metadatos, `## Cobertura`, con tres listas: subsistemas recorridos, excluidos por el plan con su motivo, y no recorridos por falta de evidencia o de tiempo. Cada subsistema se nombra exactamente como figura en el mapa de dominios. Una frase final indica que las conclusiones valen para lo recorrido. Este apartado no define hallazgos ni lleva identificadores.
   - Si el inventario, el mapa y el plan llegan vacíos, no hubo preparación: aplica el resto de las instrucciones sin este protocolo y omite el apartado «Cobertura».
5. **Alcance y consumidores externos:** el análisis no ve lo que consume el repositorio o la carpeta desde fuera. Un tipo público, una interfaz, un atributo o un método de extensión sin referencias internas no se declara muerto: es «superficie pública sin usos internos» y pasa a candidato sin confirmar. También se comprueban los miembros privados sin llamadas (un método privado sin uso es código muerto aunque la clase se use), los registros en DI que nadie resuelve y los grupos de tipos que solo se referencian entre sí (un clúster sin consumidor).
6. Clasifica cada elemento por nivel de confianza y produce el informe estructurado en Markdown con su correspondiente diagrama Mermaid.

---

{{ include '_provided_documentation.md' }}

**Identificadores de hallazgo de este informe:** el número de informe es `03`. Todos llevan el formato `03-CAT-nnn` (por ejemplo `03-CDM-001`).

@[_common_rules.md]

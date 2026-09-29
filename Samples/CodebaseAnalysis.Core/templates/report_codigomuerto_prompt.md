Genera el **Informe de Detección de Código Muerto y Elementos Huérfanos** para el repositorio analizado.

### Contexto y Ecosistema Tecnológico del Repositorio:
- **Lenguajes y Tecnologías Detectadas**: `{{ detected_languages }}`

### Datos Semánticos Pre-extraídos del Grafo:
- **Candidatos a Código Muerto por Alcanzabilidad (`dead_code_candidate = true`)**:
```json
{{ dead_code_candidates }}
```

- **Registros o Factorías no Referenciadas**:
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

- **Campos privados que nada lee (sin `READS`)**: campos Java `private` que no son `static final`, con su fichero y línea; `writes` cuenta los métodos que los escriben. Son candidatos: la reflexión, la serialización y los frameworks que leen o escriben campos por nombre (los `ActionForm` de Struts, los beans) no dejan `READS`.
```json
{{ unread_fields }}
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

---

### Instrucciones de Ejecución:
1. Revisa las listas de candidatos anteriores considerando el ecosistema del proyecto (`{{ detected_languages }}`).
   - En Access/VBA: formularios huérfanos, tablas temporales/backup (`_temp`, `_tmp`, `_bak`, `_old`, `_copy`, `_test`), consultas no vinculadas a ningún formulario o módulo.
   - En C#/.NET u otros: clases sin uso, endpoints sin handler, dependencias no resueltas.
2. Antes de declarar muerto un candidato, sigue la regla de puntos ciegos del grafo: busca quién lo llama o lo hereda con `graph_neighbors` (`direction: in`), si atiende una ruta (`HANDLES`) y si lo importa alguna JSP (`page_imports`), y comprueba la configuración que pueda referenciarlo. Lo que no puedas descartar queda como candidato sin confirmar, no como código muerto.
3. Un grupo grande de candidatos con un patrón común es señal de uso dinámico. Antes de afirmar que sus miembros están muertos, busca la factoría con `grep_search` (el sufijo, el prefijo o el paquete del grupo, en el código y en `**/*.xml`), lee con `view_file` el código que compone el nombre y comprueba que puede llegar a esas clases. Si la instanciación dinámica es plausible, lleva `likely_dynamic` o no, clasifica el grupo entero como **dudoso por instanciación dinámica**, con la evidencia (fichero y línea de la factoría o de la configuración), y no como código muerto; si no encuentras ninguna vía, dilo y déjalo como candidato sin confirmar. Un candidato con `named_in_configuration` no está muerto: lo nombra la configuración o el código.
4. Clasifica cada elemento por nivel de confianza y produce el informe estructurado en Markdown con su correspondiente diagrama Mermaid.

---

{{ include '_provided_documentation.md' }}

@[_common_rules.md]

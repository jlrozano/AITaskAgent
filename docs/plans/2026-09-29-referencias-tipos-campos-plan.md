# Plan: referencias de tipos y campos en el grafo Java

Estado: plan del 2026-09-29, **aprobado por el usuario el mismo día** («adelante»); **hecho** el mismo día (tareas 1-4). El usuario pidió que, al usarse un método, una clase o
una variable, se sepa dónde se declara. Aceptó planificarlo tras ver el estado. Amplía la tanda Java
(`2026-09-28-enriquecimiento-nativo-java-design.md`, D1-D15; plan P1-P16), cuyas decisiones no se reabren.

## Estado actual

- El analizador recorre solo declaraciones de clase y método, invocaciones y `new` (`JavaAnalyzer.java:664,726,775,787`).
  Emite los registros `file`, `diag`, `type`, `method` y `call` (`:20`).
- El escritor sustituye `CALLS`, `INHERITS` e `IMPLEMENTS` (`JavaGraphWriter.cs:78`).
- Resultado:
  - Método usado → declaración: **sí**, con `CALLS` y sus líneas.
  - Constructor implícito: no hay arista, solo el recuento `implicit_constructor`.
  - Clase usada como tipo (campo, parámetro, variable local, `cast`, `instanceof`, genérico, `X.CONSTANTE`, `X.class`):
    **no**; solo aparece como texto en `param_types` o `return_type_fqn`.
  - Campo leído o escrito: **no**. Solo existen las `USAGE` heurísticas de cbm, por nombre (176.198 en el repo
    objetivo, con propiedades `{callee}`).
  - Variables locales: fuera del grafo, a propósito. Se resuelven leyendo el cuerpo del método.
- cbm guarda los campos como `Field` con un QN que incluye el inicializador (`…DAOAccionesOracleImpl.nombreConexion = null`),
  además de un `Variable` `<prefijo>.<nombre>` por campo. Hay 46.256 `Field` y 54.193 `Variable` en el repo objetivo.

## Decisiones

- **Q1. Nuevos registros del analizador** (`schema` 2; el lector acepta 1 y 2):
  - `field`: declaración de campo, con `path`, `chain`, `ownerFqn`, `name`, `line`, `end`, `typeFqn`, `access`,
    `static`, `final` y `doc`. Incluye las constantes de enum como `field` con `enumConstant: true`.
  - `ref`: `path`, `from` (clave del método, o `null`), `fromType`, `fromField` (si la referencia está en el tipo
    declarado o el inicializador de un campo), `to` (FQN del tipo, o `ownerFqn#campo`), `kind` `type|field`, `access`
    `read|write|readwrite` (solo campos), `contexts` (`param`, `return`, `field`, `local`, `cast`, `instanceof`,
    `generic`, `static_access`, `class_literal`, `annotation`, `throws`, `catch`, `new_implicit`), `origin` y `lines`.
- **Q2. Agregación en Java** por fichero y por (origen, destino, `kind`, `access`), con las líneas únicas. No se emite una
  línea por referencia: el JSONL ya ocupa 744 MB y las referencias de tipo son muchas más que las llamadas.
- **Q3. Qué no se emite como referencia:**
  - los `import`, que no son usos;
  - los tipos de `extends`/`implements`, que ya son `INHERITS`/`IMPLEMENTS`;
  - las referencias de un tipo a sí mismo;
  - los destinos que no son del proyecto, que solo se cuentan por origen (`jdk`, `jar`, `unresolved`), igual que las
    llamadas.
- **Q4. `new` de un constructor implícito:** en vez de perderse, genera un `ref` de tipo con contexto `new_implicit`.
- **Q5. Nodos de campo.** Se casa con el `Field` de cbm del mismo tipo propietario cuyo `name` sea el nombre del campo
  o empiece por él seguido de espacio, `=` o `[`. El nodo casado conserva su QN y se le parchean `field_name`,
  `type_fqn`, `accessibility`, `is_static`, `is_final`, `docstring` y `enriched_by`. Si no casa, se crea
  `<QN del tipo>.<campo>` si ese QN está libre (misma regla que P9), con `DEFINES` desde su tipo. Los `Variable`
  gemelos de cbm no se tocan.
- **Q6. Aristas nuevas, con `enriched_by: ["JavaAnalyzer"]`:**
  - `USES_TYPE`, de método, campo o tipo a `Class`/`Interface`/`Enum`, con `lines`, `count` y `contexts`;
  - `READS` y `WRITES`, de método (o de tipo, en inicializadores) a `Field`, con `lines` y `count`.

  Como hay una sola arista por (origen, destino, tipo), el uso de un campo que se lee y se escribe genera las dos.
- **Q7. Sustitución.** Se añaden `USAGE`, `USES_TYPE`, `READS` y `WRITES` a los tipos que se borran y reescriben cuando
  el origen está en un fichero analizado. Así se retiran las `USAGE` heurísticas de cbm en Java, igual que se hizo con
  `CALLS`.
- **Q8. Versiones:** `analyzerVersion` 2, `schema` 2 y `JavaGraphWriter.EnricherVersion` 2. Con eso, P3 vuelve a
  ejecutar el analizador sobre grafos ya enriquecidos.
- **Q9. Consumidores:**
  - `graph_neighbors` incluye por defecto `USES_TYPE`, `READS` y `WRITES`, y gana un filtro `line`: las aristas cuyas
    `lines` contienen esa línea. Sirve de «qué referencia hay en la línea N de este método», sin índice de columnas.
  - Facetas: el `fan_in` calculado de una clase cuenta también los `USES_TYPE` entrantes, para que un bean usado solo
    como tipo no salga como código muerto. Faceta nueva `UnreadFields`, con los campos privados sin `READS`, para
    los informes 03 y 02.
  - `_graph_rules.md` y `chat_system.md`: una línea sobre las aristas nuevas y sobre el filtro `line`.
- **Fuera de alcance:**
  - variables locales;
  - `view_code_item` para Java, que es un pendiente aparte;
  - un «ir a definición» por columna;
  - referencias desde scriptlets JSP (D9).

## Tareas

Cada tarea la hace un agente, en secuencia, porque las tres primeras compilan Core.

1. **Analizador** (`Enrichment/Java/JavaAnalyzer.java`):
   - `visitIdentifier`, `visitMemberSelect`, `visitVariable`, `visitTypeCast`, `visitInstanceOf`,
     `visitParameterizedType`, `visitAnnotation` y los tipos de retorno, `throws` y `catch`;
   - clasificación del elemento resuelto (tipo, campo o enum constante) y del acceso (destino de `visitAssignment` o
     `visitCompoundAssignment` → escritura, `++`/`--` → lectura y escritura);
   - registros `field` y la agregación de Q2;
   - verificación, sin tocar .NET, sobre el repo de prueba y sobre el repo objetivo: recuentos por contexto y origen,
     porcentaje resuelto, tamaño del JSONL, tiempo y memoria, comparados con los actuales (61 s y 744 MB).
2. **Lector y escritor** (`JavaAnalysisRecords.cs`, `JavaGraphWriter.cs`):
   - Q5-Q8;
   - tests sobre el grafo sintético: campo casado con el `Field` de cbm con inicializador, campo creado, `READS`/
     `WRITES`, `USES_TYPE` por contexto, `new_implicit`, sustitución de `USAGE` e idempotencia;
   - volver a medir la escritura.
3. **Consumidores:** Q9 en `graph_neighbors`, `SqlFacetExtractorStep` y plantillas, con tests; `[JdkFact]` de
   extremo a extremo sobre `JavaTestRepo`, ampliado con un bean usado solo como parámetro, un campo privado sin leer,
   un `cast` y una constante estática.
4. **Aceptación sin LLM** sobre una copia del repo objetivo en `C:\Users\juan.rozano\m\Online`: la ruta corta es
   obligatoria por el límite MAX_PATH de cbm.
   - Reindexar con cbm, ejecutar `LanguageEnricherStep` con el arnés de T6 y comprobar los criterios.
   - Actualizar la guía `docs/guides/codebase-analysis-flujo-y-politica-de-informes.md`, los README y el documento de
     estado.
   - Limpiar el proyecto de cbm al terminar.

## Criterios de aceptación

1. En el repo de prueba:
   - `Servicio` tiene `USES_TYPE` a `Dao` (tipo local) y a `Bean` (retorno);
   - el campo privado sin leer sale en `UnreadFields`;
   - `new DaoImpl()`, con constructor implícito, deja un `USES_TYPE` con contexto `new_implicit`;
   - la lectura y la escritura de un campo dan `READS` y `WRITES` con sus líneas.
2. En el repo objetivo, el 99 % o más de las referencias a tipos y campos del proyecto quedan resueltas. En los
   ficheros analizados no queda ninguna `USAGE` de cbm.
3. En el repo objetivo, el 95 % o más de los campos casan con un `Field` de cbm; el resto se crean o se cuentan como no
   casados, con motivo.
4. Dos pasadas dejan los mismos recuentos y la BD de `~/.cache` no cambia.
5. El paso Java completo no pasa de 1,5 veces el tiempo actual en la misma máquina: referencia de 109 s en ruta corta.
   El JSONL no pasa de 2 veces el tamaño actual. Si se superan, se informa con cifras y se propone el recorte: por
   ejemplo, quitar los contextos `local` o `generic`.
6. `graph_neighbors` con `line` devuelve las referencias de esa línea en un test.
7. Las suites de Core y Web pasan, y los tests con JDK también.

## Seguimiento

**Tarea 1 (hecha, en `8d73646`).** Mediciones sobre el repo objetivo, contrastadas con la versión anterior en la misma
sesión:

| Medida | Antes | Ahora | Cambio |
|---|---|---|---|
| Tiempo del analizador | 56,6 s | 61,9 s | 1,09x |
| Tamaño del JSONL | 744 MB | 1.003 MB | 1,35x |

Refs de proyecto resueltas: 99,996 %. Desviaciones de Q1:
- se añade el campo `count`;
- contextos nuevos `new_array` y `other`;
- `contexts` solo se rellena en los refs de tipo;
- `origin` siempre vale `project`;
- `new_implicit` también cubre las anónimas.

**Tarea 2 (hecha, `8d73646`).** Sobre `C:\Users\juan.rozano\m\Online`:
- 0 `USAGE` de cbm en ficheros `.java`;
- campos casados: 95,85 %;
- la segunda pasada deja el grafo idéntico;
- aristas nuevas: 253.753 `USES_TYPE`, 121.659 `READS` y 56.250 `WRITES`.

Desviaciones:
- las constantes de enum se anclan en el `Variable` de cbm `<QN tipo>.<constante>`, porque cbm no crea `Field` para
  ellas;
- los campos de interfaz, de enum y de anotación se crean y no cuentan para la deriva;
- las `USAGE` solo se sustituyen con schema 2.

**Criterio 5 abierto.** En la misma sesión, v2 tarda unas 1,4 veces lo que v1 (1,28 y 1,49 en dos pares contiguos).
Contra la cifra fija de 109 s, 2 de 3 pasadas superan 163,5 s, con la máquina cargada. Recomendación: aceptarlo sin
recortar y repetir la medición en reposo en la tarea 4. El recorte posible sería no emitir los `READS` de acceso
estático a constantes. Pendiente del usuario.

**Tarea 3 (hecha, en `56d8757`).** Q9: `graph_neighbors` con `USES_TYPE`, `READS`, `WRITES` y el filtro `line`, el
`fan_in` de los tipos con `USES_TYPE`, la faceta `UnreadFields` y las plantillas.

**Tarea 4 (hecha).** Aceptación sin LLM sobre `C:\Users\juan.rozano\m\Online`, con el arnés de T6 y copias nuevas de
la BD de cbm en cada pasada. Antes de medir, un cambio en `ZeroFaninClasses`: con `USES_TYPE` en el grafo ya no exige
constructor declarado, porque el `new` con constructor implícito deja arista, y excluye los tipos que Struts instancia
por reflexión (`form_type` y `action_type` de sus `Route`, por FQN). Test nuevo en `FacetExtractionTests`.

| Criterio | Valor | Estado | Evidencia |
|---|---|---|---|
| 1. Repo de prueba | `USES_TYPE` a `Dao` y a `Bean`, `UnreadFields`, `new_implicit`, `READS`/`WRITES` con líneas | cumplido | `[JdkFact]` de la tarea 3 (`GraphToolsTests`, `FacetExtractionTests`), en verde en la suite |
| 2. Resueltas y sin `USAGE` | 99,996 % (23 sin resolver de 587 267); 0 `USAGE` de cbm en `.java` | cumplido | `q4/counts_pass1.json` del scratch; igual que en la tarea 2 |
| 3. Campos casados | 95,85 % (52 962 de 55 257); 2295 creados, 0 sin nodo | cumplido | `details.fields` de `enrichment_runs` |
| 4. Repetición | mismos recuentos en dos pasadas; SHA-256 de la BD de `~/.cache` igual antes y después | cumplido | `counts_pass1.json` frente a `counts_pass2.json`, `cache-sha-*.txt` |
| 5. Tiempo y tamaño | 1,39 veces la mediana de v1 (166,6 s frente a 119,4 s, 6 pares alternos: v1/v2 121,4/129,3, 117,4/192,1, 163,5/187,8, 106,6/137,6, 99,5/182,5 y 166,8/150,6 s); JSONL 1,35 veces | cumplido, con la máquina cargada | logs `q4/c5-*.log` |
| 6. `graph_neighbors` con `line` | devuelve las referencias de la línea | cumplido | test de la tarea 3 en `GraphToolsTests` |
| 7. Suites | Core 486 (485 más el test nuevo) y Web 111 en verde, con JDK | cumplido | `dotnet test` con `CODEBASE_ANALYSIS_TEST_JDK` al cerrar la tarea 4 |

**Criterio 5 cerrado.** Pasadas alternas v1/v2 sobre copias nuevas, con la máquina en uso normal (Teams, navegadores) y
el disco al 97 %. Los tres primeros pares, v1 → v2, dieron 1,55 (medianas 121,4 y 187,8 s), por encima del límite; el
desglose mostró que las pasadas lentas de las dos versiones coinciden con la copia de 1 GB de la BD tardando 8-10 s en vez
de 0,4, es decir, con carga de disco ajena al paso, así que se midieron tres pares más en orden v2 → v1 (1,41). Con los
seis pares la razón de medianas es 1,39 (166,6 s frente a 119,4 s), por debajo de 1,5, y en el par más tranquilo fue 1,07.
No se recorta: quitar las `READS` a constantes `static final`, el candidato más barato, ahorraría 56 003 de las 431 662
aristas nuevas (13 %) y perdería dónde se usa cada constante; la lectura del JSONL (de 7-12 a 10-17 s) y el `commit`
(2-9 s en las dos versiones) no dan más. La cifra fija de 109 s no sirve de referencia: v1 mismo pasa de ella en 4 de 6
pasadas. Pendiente, si se quiere una cifra limpia: repetir en reposo.

Facetas sobre el grafo enriquecido: 45,9 s, y 46,7 s en una copia sin el índice por label, con las mismas facetas;
24 s son de `PrepareDerivedMetricsAsync`. `ZeroFaninClasses` da 817 tipos (851 sin la exclusión de Struts, 638 con la
regla anterior del constructor): de 5 revisados, 3 verdaderos, 1 falso y 1 sin decidir, y 400 de los 817 son DAO
`*Oracle*Impl` que la factoría instancia por `Class.forName` (`UtilidadesDAO.java:202-206` de la copia), falsos
positivos todos. `UnreadFields` da 154 campos, y los 5 revisados son verdaderos.

## Riesgos

- El volumen. Mi estimación, sin medir: entre 1 y 2 millones de aristas nuevas. Puede empeorar la escritura y las
  facetas; se mide en las tareas 1, 2 y 4.
- El casado de campos depende del formato de `name` de los `Field` de cbm. Si cbm cambia, lo detecta el aviso de deriva
  de H-9, ampliado a los campos.
- Las referencias que `javac` no resuelve (por el classpath incompleto) cuentan como `unresolved`. Nunca generan aristas.

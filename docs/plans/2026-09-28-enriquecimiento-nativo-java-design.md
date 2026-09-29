# Enriquecimiento nativo del grafo: analizador Java (primera tanda)

Estado: diseño aprobado en el brainstorm del 2026-09-28, pendiente de `/plan`. Ámbito: `Samples/CodebaseAnalysis.Core`
y la web `Samples/CodebaseAnalysisWeb`. Caso objetivo: `C:\BBVA\Fuentes\cli-bbva-clan\TEDT\Construccion\delta\Online`.

## Propósito

La app analiza hoy un repositorio Java con el grafo que genera cbm y un pase del LLM sobre quince ficheros
(`Steps/BdpEnricherStep.cs:84-91`), porque el único enriquecedor semántico que conoce es BdpGraphEnricher.CLI, que es
externo, está hecho para BDP y solo abre soluciones .NET. Medido sobre el repo objetivo, cbm deja el esqueleto de clases
y métodos pero no resuelve la semántica: las aristas `CALLS` se deciden por nombre y 13 de 25 muestreadas apuntan a un
destino equivocado, `INHERITS` tiene una sola arista e `IMPORTS` ninguna, las sobrecargas se funden en un nodo, los
textos en ISO-8859-1 se guardan sin transcodificar y las 1736 JSP no se indexan. Esta primera tanda da a la app un
analizador Java propio, basado en el compilador `javac`, que corrige ese grafo en lugar de sustituirlo, y un plugin web
para Struts y JSP que permite detectar código de negocio o de servidor metido en las pantallas y enlazar los flujos
desde la pantalla donde parten.

## Alcance

**Entra en esta tanda:**

1. Selección de enriquecedores por lenguaje a partir de los ficheros del repositorio.
2. Analizador Java con `javac` (Compiler Tree API) ejecutado como proceso aparte, con la JVM configurable.
3. Contrato de escritura sobre la copia del grafo: marca de origen, idempotencia, sustitución de aristas y tabla
   `enrichment_runs`.
4. Semántica del lenguaje: jerarquía (`INHERITS`, `IMPLEMENTS`), llamadas resueltas (`CALLS`), sobrecargas como nodos
   propios, propiedades de método (tipos completos, accesibilidad, anotaciones, Javadoc bien decodificado,
   `cyclomatic`).
7. Exclusión del código de test del repositorio analizado: no se analiza y se retira del grafo.
5. Plugin web Struts/JSP de nivel estructural: acciones de `struts-config`, forwards, formularios, includes, Tiles y
   detección de scriptlets.
6. Mensajes de progreso y avisos en la fase `enrich` de la web.

**Queda fuera, en este orden previsto:** corregir los consumidores que leen propiedades que nadie escribe
(`SqlFacetExtractorStep.cs:82,90,108,118,128,339`), portar los enriquecedores de C# a Roslyn en proceso, resolver el Java
de los scriptlets traduciendo las JSP con Jasper, construir nodos `Flow` de extremo a extremo, `web.xml`, DWR y EJB,
clases anónimas y enums de primer nivel como nodos, y un enriquecedor basado en LLM para repositorios sin lenguaje
soportado.

## Decision Log

| # | Decisión | Por qué | Coste aceptado |
|---|---|---|---|
| D1 | Un enriquecedor por lenguaje que extrae solo semántica del lenguaje; lo de cada framework va en plugins opcionales | Tipos, herencia, llamadas y comentarios no dependen de Struts ni de Spring; mezclarlos ataría el analizador a un framework | Dos niveles de componentes que coordinar |
| D2 | Selección por búsqueda de ficheros en disco: `.java` activa Java, `.cs` activa C#, los dos activan ambos y ninguno no activa ninguno | Es la señal más simple y fiable del lenguaje; decidido por el usuario | Un repo con un `.java` suelto activa el analizador entero |
| D3 | Anclarse en los nodos de cbm por `qualified_name` y sustituir sus aristas heurísticas, en lugar de generar el grafo del lenguaje desde cero | Los QN de cbm salen de la ruta del fichero y el analizador los reproduce en el 100 % de los tipos y el 99,96 % de los métodos; así se conservan vectores, FTS y consumidores | Hay que replicar las reglas de QN de cbm, incluidas sus rarezas |
| D4 | `javac` 17 con `--release 8`, desde un JDK portable | Medido sobre el repo entero: 66 s, 2,8 GB y 47 fallos entre 1.066.814 llamadas de `src/`; es el único que sigue analizando cuando falta un tipo en el classpath | Hace falta un JDK, que no está en las máquinas objetivo (solo JRE 8); se despliega descomprimido, sin permisos de administrador, como cbm |
| D5 | La configuración de la JVM es un parámetro del sistema | Memoria, lotes y rutas dependen de la máquina y del tamaño del repo; decidido por el usuario | Más superficie de configuración que validar |
| D6 | Un solo lote por defecto; lotes y lotes en paralelo como opción | Cada lote vuelve a resolver los tipos compartidos (JDT: 72 s en un lote frente a 200 s en lotes de 2000) y en paralelo la memoria se multiplica | Un repo mucho mayor necesita ajustar la configuración |
| D7 | La declaración de una sobrecarga que coincide con la línea del nodo de cbm conserva su QN; las demás llevan el sufijo de tipos de parámetros (`…consultar(Hashtable,long)`) y `overload_of` | Hay 4105 QN sobrecargados que suman 9626 declaraciones; conservar el QN de cbm en una de ellas mantiene vectores y consumidores | Cambia el contrato del QN para las sobrecargas, que antes no existían como nodos |
| D8 | Nunca se escribe en la BD original de cbm; todo lo escrito lleva `enriched_by`; cada pasada borra su salida anterior; se sustituye en lugar de duplicar; cada pasada deja una fila en `enrichment_runs` | Permite comparar la copia enriquecida con la original de la misma indexación y repetir sin acumular | Tras una reindexación de cbm hay que volver a enriquecer |
| D9 | Las JSP entran en esta tanda a nivel estructural y con detección de scriptlets; la resolución exacta de su Java queda para después | El objetivo es encontrar código de negocio en las pantallas y cerrar los flujos, y la detección no necesita compilar | Las llamadas desde scriptlets no forman parte todavía del grafo resuelto |
| D10 | Java primero; C# después | Java es el caso objetivo y hoy no tiene enriquecedor; C# conserva mientras tanto el camino actual | La paridad con BdpGraphEnricher en C# se comprueba en la tanda siguiente |
| D11 | El proceso Java escribe un fichero de resultados y es .NET quien escribe en SQLite | El contrato de escritura (D8) queda en un único sitio y en el lenguaje de la app | Un formato intermedio que versionar |
| D12 | El JDK se da por presente y su ruta va en `appsettings` (`JavaAnalyzer:JdkPath`) | Decidido por el usuario; la app no instala ni descarga JDK | Sin JDK configurado, Java se queda con el grafo de cbm y un aviso |
| D13 | El analizador es un único `.java` ejecutado con el *source launcher* | Sin paso de compilación en el build ni binarios en git | Un par de segundos de compilación en memoria por ejecución; si crece, compilar y cachear el jar |
| D14 | Los jars *provided* van solo en configuración, sin valores por defecto, y la pasada lista los paquetes que faltan | Evita distribuir librerías de terceros y deja claro qué añadir | El primer análisis de un repo puede salir con la capa web a medio resolver |
| D15 | El código de test del repositorio analizado no entra en el grafo: no se analiza y sus nodos de cbm se retiran de la copia | Decidido por el usuario; los tests no aportan al análisis de la aplicación y sus librerías (JUnit, Mockito) no están en el repo | Se pierde la trazabilidad test → código |

## Diseño

### Selección por lenguaje

Tras indexar, el pipeline busca en disco los ficheros `.java` y `.cs` del repositorio, excluyendo las carpetas que
nunca son fuentes (`.git`, `.codebase`, `target`, `build`, `bin`, `obj`, `node_modules`) y las raíces de test. La
búsqueda en disco es la fuente fiable porque refleja lo que hay en el repositorio y no depende de qué haya decidido
indexar cbm; los ficheros que el analizador procese sin nodo en cbm se cuentan como no casados en `enrichment_runs`.
La lista resultante es también la entrada del analizador. La presencia de `.java` activa el analizador Java y la de `.cs`
mantiene el camino actual de `BdpEnricherStep` (CLI si está disponible, fallback del LLM si no) hasta que llegue el
port a Roslyn. Si no hay ninguno de los dos, se mantiene el fallback del LLM actual sin cambios. Los plugins de framework
se activan por su propia señal, que en Struts es la presencia de algún `struts-config*.xml`. El analizador no depende de
la bandera `IsEnriched`, porque la ingesta de Office la fuerza a `true` (`DetectAndIndexGraphStep.cs:100`) y con ella
se saltaría el enriquecimiento; su propia condición de ejecución es que no haya en `enrichment_runs` una pasada correcta
sobre la indexación actual.

### Analizador Java

El analizador es un programa Java de un solo fichero fuente, que se copia a la salida del build como contenido y se
ejecuta en modo *source launcher* (`java --add-exports … JavaAnalyzer.java`, disponible desde Java 11). Así no hay paso
de compilación en el build de .NET ni binarios en git, y el único requisito es el JDK que ya exige el análisis; el coste
es compilarlo en memoria en cada ejecución, un par de segundos frente al minuto largo del análisis. Si el programa
creciera más allá de un fichero, se pasaría a compilarlo con el mismo JDK en la primera ejecución y cachear el jar por
huella del fuente. Recibe por argumentos o por un
fichero de petición las raíces de fuentes, el classpath y la configuración, y ejecuta `JavacTask.parse()` y
`analyze()` sin generar clases (`-proc:none`, sin salida de bytecode). Recorre los árboles con `TreePathScanner` y
emite, por cada fichero, las declaraciones con su posición y su cadena de nombres anidados (`chain`), de la que .NET
compone el QN estilo cbm con la ruta del fichero (`JavaGraphWriter`), la jerarquía, las llamadas y creaciones
resueltas con su destino clasificado como proyecto, JDK o jar, y los diagnósticos. Las llamadas cuyo destino es el JDK
o un jar no generan aristas, solo cuentan en el resumen de la pasada.

El classpath se compone de los jars del repositorio (en el caso objetivo, los 109 de `WEB-INF/lib`) más un classpath
*provided* configurable. Son las librerías que el código usa pero que no viajan con él: las que aporta el servidor de
aplicaciones en ejecución (servlet-api, jsp-api), que Maven declara con scope `provided` y por eso no están en
`WEB-INF/lib`. En el repo objetivo faltan las dos, y sin ellas quedan sin resolver los tipos de la capa web. La
configuración acepta ficheros y carpetas de jars y no trae ninguno por defecto; a cambio, el
resumen de la pasada lista los paquetes que no se han encontrado (`package javax.servlet does not exist`), de modo que
el usuario sabe qué jar añadir. La codificación se decide fichero a fichero: si el contenido es UTF-8 válido se lee
como UTF-8 y si no con la codificación por defecto configurada, que en el caso objetivo es ISO-8859-1 según su
`pom.xml`, ya que dos ficheros del repo están en UTF-8 y leídos como Latin-1 salen corruptos. Distinguir el origen de un
símbolo exige `--add-exports` de una API interna de `javac`, y eso se asume como dependencia de la versión del JDK.

.NET lanza el proceso con la JVM de la configuración, lee su salida estándar para informar del progreso y recoge el
resultado del fichero que deja en la carpeta de trabajo del análisis. Si no encuentra el JDK o el proceso termina con
error, el paso emite un aviso y el pipeline continúa con el grafo de cbm, ya que un repositorio sin semántica resuelta
sigue siendo analizable.

### Configuración

La sección `JavaAnalyzer` de `appsettings.analysis.json` admite `Enabled`, `JdkPath` (ruta de un JDK 17 o superior,
que se da por presente en la máquina), `JvmOptions` (por defecto `-Xmx8g -XX:+UseG1GC`), `BatchSize` (0 = un solo lote),
`MaxParallelBatches` (por defecto 1), `ProvidedClasspath`, `TestRoots`, `DefaultEncoding` y `TimeoutSeconds`. Como se lee con el
mecanismo existente (`Hosting/AnalysisConfiguration.cs:43-65`), admite variables de entorno, user secrets y línea de
comandos, de modo que cada máquina ajusta la memoria sin tocar el fichero del sample.

### Escritura en el grafo

.NET aplica el resultado sobre la copia `.codebase/codebase.db` en una transacción por pasada, y nunca abre la BD de
`~/.cache/codebase-memory-mcp/`. Al empezar borra lo que escribió la pasada anterior de ese mismo enriquecedor,
identificado por `enriched_by`, y a continuación:

0. **Retira el código de test**: los nodos cuyo `file_path` cae bajo una raíz de test, y sus aristas, se eliminan de la
   copia, porque cbm también los indexa. Las raíces de test son el `testSourceDirectory` del `pom.xml` si existe (en el
   caso objetivo, `./test`), `src/test/java` por convención y las que añada la configuración (`TestRoots`).
1. **Casa** cada declaración con su nodo por `(project, qualified_name)`; si hay sobrecargas, la que coincide con el
   `start_line` del nodo conserva el QN y las demás se crean con sufijo de tipos y `overload_of` (D7).
2. **Completa** las propiedades del nodo: `signature` normalizada, `param_types` completos (incluidos los primitivos,
   que cbm omite), `return_type_fqn`, `accessibility`, `is_static`, `is_abstract`, `annotations`, `docstring`
   decodificado y `cyclomatic`.
3. **Sustituye** las aristas: en los métodos de los ficheros analizados se eliminan las `CALLS` de cbm y se insertan las
   resueltas; se escriben `INHERITS` e `IMPLEMENTS` desde la jerarquía resuelta. Las llamadas a un método de interfaz
   apuntan al método de la interfaz y el despacho a implementaciones se deduce por `IMPLEMENTS`, sin multiplicar aristas.
4. **Registra** la pasada en `enrichment_runs`: enriquecedor y versión, versión del JDK, huella de la indexación de cbm,
   ficheros analizados, nodos creados, aristas de cbm sustituidas, aristas nuevas, llamadas sin resolver agrupadas por
   motivo, diagnósticos, duración y resultado.

Como todo lo escrito lleva `enriched_by`, la comparación con la BD original de la misma indexación indica arista por
arista qué se sustituyó y qué se añadió.

### Plugin web Struts/JSP

El plugin lee con .NET los `struts-config*.xml`, las definiciones de Tiles y las JSP, sin compilar nada, porque todo lo
que necesita está en etiquetas y atributos. Crea un nodo
por acción de Struts con su `path`, enlazado con `HANDLES` desde la clase de acción; un nodo por JSP, que cbm no
indexa; y las aristas de forward (acción → JSP o definición de Tiles), de formulario (JSP → acción, desde
`<html:form action>`), de include (JSP → JSP) y de definición de Tiles (definición → JSP). En cada JSP registra el
número de scriptlets y sus líneas, los `<%@ page import %>` y si contiene SQL o acceso a sesión, y marca como sospechosa
la que importa DAO, negocio o `java.sql`, o que tiene SQL embebido. Los nombres exactos de labels, aristas y
propiedades se fijan en el plan, alineados con los que ya leen los consumidores (`Route`, `HANDLES`) para que las
herramientas del chat y los informes los vean sin cambios.

### Integración y mensajes

El analizador entra como un paso nuevo del pipeline, detrás de `DetectAndIndexGraphStep` y antes de las facetas, y
reporta en la fase `enrich` de la web; como esa fase se identifica hoy por el nombre `BdpEnricherStep`
(`ClientApp/src/state/runReducer.ts:20`, `Progress/AnalysisEventMapper.cs:40-41,53`), el mapeo tiene que ampliarse al
paso nuevo. Los mensajes (`context.Report`) cubren lenguajes detectados, JDK encontrado o no, progreso por ficheros,
porcentaje de llamadas resueltas y avisos por classpath incompleto, ficheros con errores o JDK ausente.

## Alternatives Considered

- **JavaParser con su symbol solver:** descartado. Tardó 161 s y 3,9 GB en 200 ficheros, lo que proyecta horas para el
  repo, y falla con un NPE en ternarios con ramas de distinto tipo (159 fallos en la muestra).
- **Eclipse JDT 3.26** (el último que corre en JRE 8): misma calidad que `javac` y 72 s para el repo, pero con un tipo
  indirecto ausente del classpath aborta el lote sin avisar (devolvió 97 de 200 ficheros), y necesita 4,6 GB en un
  lote. Solo tendría sentido si fuera obligatorio usar el JRE 8 de la máquina.
- **IKVM para ejecutar JDT dentro de .NET:** no probado; no sirve para `javac` 17.
- **Que el enriquecedor genere todo el grafo del lenguaje (opción b):** descartado por D3, porque obliga a rehacer lo
  que cbm hace bien (vectores, FTS, ficheros no Java).
- **Lotes paralelos por defecto:** descartado por D6.
- **Aplazar las JSP entero:** descartado por D9, porque el objetivo principal del usuario pasa por ellas.

## Open Questions

N/A: todo definido; los nombres exactos de labels, aristas y propiedades del plugin web se fijan en el plan.

## Criterios de aceptación

Sobre el repo objetivo, con el JDK portable y los jars *provided*:

1. Con `.java` presentes se ejecuta el analizador Java, y con un repo sin `.java` ni `.cs` el pipeline sigue igual que
   hoy.
2. En `src/`, al menos el 99,9 % de las llamadas a métodos del proyecto quedan resueltas, y las aristas `CALLS` de los
   ficheros analizados son solo las resueltas.
3. `Accion.java:889` tiene `CALLS` a `DAOAcciones.consultar(Hashtable,long)` y la llamada de
   `DAOExtraccionEventosOracleImpl.java:2100` va a `Usuario.getCodigoUsuario()`.
4. Existen los 5521 nodos de sobrecarga que faltaban (9626 declaraciones menos 4105 QN) con su `overload_of`.
5. Hay un nodo por cada JSP y por cada acción de Struts, con sus forwards, formularios e includes, y las JSP con
   imports de DAO o SQL embebido están marcadas como sospechosas.
6. Repetir el enriquecimiento sobre la misma copia deja los mismos recuentos, y la BD de `~/.cache` no cambia.
7. Cada pasada deja una fila completa en `enrichment_runs`.
   Tras la pasada, la copia no contiene ningún nodo cuyo `file_path` esté bajo `test/`.
8. Sin JDK, el paso emite un aviso visible en la web y el análisis termina con el grafo de cbm.
9. La fase Java completa tarda menos de 5 minutos en la máquina del usuario sobre el repo objetivo (8420 ficheros). Es
   una referencia de rendimiento para este repo, no un límite de ejecución: los cortes reales son configurables
   (`JavaAnalyzer:TimeoutSeconds` para el proceso Java y `JavaAnalyzer:StepTimeoutSeconds` para el paso entero, 0 = sin
   límite), porque cada repositorio tiene su tamaño.
10. Los tests del analizador usan un repositorio de prueba pequeño (sobrecargas, interfaz e implementación, un fichero
    Latin-1 y otro UTF-8, un `struts-config`, una JSP con scriptlet) y se omiten con aviso si no hay JDK.

## Change Log

- 2026-09-28: versión inicial tras el brainstorm y las pruebas de cbm, JavaParser, JDT y `javac` sobre el repo objetivo.
- 2026-09-28: cerradas con el usuario tres preguntas abiertas (D12 JDK en `appsettings`, D13 *source launcher*, D14 jars
  *provided* en configuración); descartada la de políticas corporativas.
- 2026-09-28: D15, el código de test queda fuera del grafo (sin JUnit ni Mockito); detección de `.java`/`.cs` por
  búsqueda en disco.
- 2026-09-28: implementada según el plan `2026-09-28-enriquecimiento-nativo-java-plan.md`, que fija nombres y reglas
  (P1-P16). Cambios sobre esta spec decididos al implementar: (1) una JSP que importa un paquete de negocio solo es
  sospechosa si el tipo importado se usa en un scriptlet o una declaración, porque con la regla literal 787 de 1736
  JSP salían marcadas solo por importar utilidades y constantes; los imports de DAO o `java.sql` y el SQL embebido
  siguen marcando por sí solos (patrones `SuspiciousImportPatterns` y `BusinessImportPatterns`); (2) criterio 4: con
  D15 se cuentan solo las sobrecargas de `src/`, 5488 frente a las 5521 que incluían `test/` (una más queda sin nodo
  porque su QN lo ocupa un `Field` de cbm); (3) criterio 7: una pasada que se salta porque la indexación ya está
  enriquecida no escribe fila, solo las que se ejecutan o fallan.
- 2026-09-29: errata. El analizador no emite el QN: emite `chain` y la ruta, y el QN estilo cbm lo compone .NET
  (`JavaGraphWriter.PathPrefix` más `chain`). Corregido el párrafo del analizador; D3 no cambia.

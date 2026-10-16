# Uso de Roslyn y javac en el enriquecimiento del grafo (2026-09-30)

Informe previo, sin cambios de código. Lo he escrito a partir de dos revisiones de solo lectura, una de
`Samples/DotNetEnricher` y otra de `Samples/CodebaseAnalysis.Core/Enrichment/Java`. Las referencias `fichero:línea` las
verificaron esos agentes. Yo he comprobado a mano `ReachabilityEnricher.cs:211-220`.

## Conclusión

Los dos analizadores sacan el esqueleto del compilador: tipos, herencia, métodos, llamadas y, en Java, lecturas y
escrituras de campos. Lo que decide los informes lo siguen calculando SQL sobre las aristas por nombre de cbm o
heurísticas de nombres, y eso incluye el código muerto, el uso de DI, `fan_in`, los hotspots y los patrones de error.
En C# no se usa ninguna de las APIs fuertes de Roslyn: no aparece `SymbolFinder`, `GetDiagnostics`, `AnalyzeDataFlow`,
`IOperation`, trivia ni `CompilationWithAnalyzers`. En Java no se usa nada del cuerpo de los métodos salvo la
ciclomática.

Varias facetas de los informes leen propiedades que no escribe nadie: `throws`, `detected_issues`, `anti_pattern`,
`smell`, `hotspot` en Java y `field_name` en C#. Por eso `suspect_patterns`, `critical_hotspots_exceptions` y
`unread_fields` (este último en C#) llegan vacías. Lo que el modelo encuentra en ese terreno lo encuentra leyendo
código, y eso no escala al repo grande.

## Fallos sembrados

| Repo | No casan en el comparador | Faltan de verdad | Causa del resto |
|---|---|---|---|
| java-simple | S05, S08 (×2), S09, S15 | S05 (NPE) | el parser del validador no lee 02 ni 08 |
| csharp-simple | 9 | SEED-12a (DI nunca resuelto) | el parser no lee 02 ni 08; citas `:11,16-21` |

SEED-12a tiene dos causas. `unused_registration` se calcula con aristas `USAGE` de cbm, y `IInvoiceService` recibe
una desde la lista de bases de su propia implementación. Además, `unused_di_registrations` solo llega a la plantilla
de código muerto y no a la de DI.

## Qué se decide con cbm o heurística cuando el compilador lo da exacto

- **C#:** reachability, código muerto, `unused_registration`, `unused_config`, `fan_in`/`fan_out`, hotspot,
  acoplamiento, `PROJECT_DEPENDS_ON`, `REGISTERED_VIA` (con `LIKE`), clases de configuración, rol de proyecto, tests y
  `BackgroundService` por nombre. `IMPLEMENTS` solo mira las interfaces directas. El lifetime de DI se saca con
  `Contains`.
- **Java:** el `HANDLES` de Struts se resuelve por nombre simple y sufijo de ruta, aunque el nodo ya tiene `fqn`. La
  reflexión se detecta con regex. `X::m` no genera `CALLS`. No hay `OVERRIDES`. Nadie escribe `dead_code_candidate`.
  `@Deprecated` se busca con `LIKE`. La cobertura de documentación cuenta también los `//` de cbm.

## Lo que el compilador da y no usamos

| Prioridad | Dato | C# | Java | Esfuerzo | Informe | Semillas |
|---|---|---|---|---|---|---|
| 1 | Referencias exactas: miembros privados sin uso, `unused_registration`, `fan_in` real, `::` y `OVERRIDES` en Java | `SymbolFinder` (hay que conservar la `Solution`) | `Elements.overrides`, `MEMBER_REFERENCE` | M | código muerto, DI, deuda | 12a, 10a (C#); S10a (Java) |
| 2 | Patrones de error por método (`detected_issues`): catch vacío, recurso sin cerrar, `throws` | `GetDiagnostics()` (CS8602, CS0414/0169, CS4014, CS0162, CS0618), `async void`, `.Result`/`.Wait()`, `IDisposable` sin `using`, captive dependency | `String ==`, `AutoCloseable` fuera de try-with-resources | S cada uno | errores, DI | 02, 03a/b, 04, 12b, 15; S02, S03, S04 |
| 3 | Campos con lecturas y escrituras | nodos Field con `READS`/`WRITES` y `field_name` (ya los consume `unread_fields`) | ya existe | M | código muerto, smells | 15 |
| 4 | Comentarios: TODO/FIXME con línea, código comentado (un comentario que parsea como código), comentario anterior a cada declaración | trivia | lexer sobre el texto ya decodificado; reescribe los 914 docstrings con U+FFFD | S-M | deuda, código muerto, chat | — |
| 5 | Rutas sin autenticación: `auth_required=false` explícito | sí | — | S | APIs | 13 |
| 6 | Constantes resueltas: credenciales, argumento de `Class.forName` | — | `getConstantValue` | S-M | APIs, código muerto | S07, T11 |

> **Fila 6 hecha el 2026-10-01.** `JavaAnalyzer.java` (analizador 4, registro `const`) y `ConstantEnricher` (C#) escriben `hardcoded_secrets` (`[{kind, name, line, preview_masked}]`, valor siempre enmascarado) y `reflection_targets` (`string[]`, con `*` por cada tramo no constante: `com.acme.orders.export.*Impl`). Las facetas `resolved_secrets` (plantillas 05 y 07) y `reflection_targets` (03) las resumen con totales y las primeras con su cita. Las clases que un patrón puede nombrar quedan vivas (`dead_code_suppressed_by`), con lo que la trampa T11 deja de salir como candidata; S07 sale en el grafo (`DbConfig.PASSWORD`, línea 11) y SEED-07b en C# (`CustomerRepository.OpenConnection`, línea 50).
| 7 | Nulabilidad: método que devuelve `null` y llamante que desreferencia | `NullableAnnotation`, CS8602 | análisis propio | M-L | errores | 05; **S05** |
| 8 | Parámetros y locales sin usar, `isDeprecated`, `DocTrees` | DataFlow | `DocTrees` | S-M | smells, deuda | — |
| 9 | Clones por hash del AST normalizado, LCOM, SQL por concatenación | sí | sí | M | smells, errores | 14, 01; S09, S15 |
| 10 | Analizadores estándar (`CompilationWithAnalyzers` con NetAnalyzers) | cubren casi toda la fila 2 | — | M-L | errores, smells | varios |

## Comentarios y codificación

- Los comentarios llegan al chat, truncados a 300 caracteres (`GraphSearchTool.cs:161,217-220`). A los informes solo
  llegan como recuento de cobertura.
- En Java, los Javadoc que escribe JavaAnalyzer salen bien decodificados. Los 914 U+FFFD están en `docstring` de cbm
  sacados de comentarios `//`, que JavaAnalyzer no sobrescribe. Ya conoce la codificación de cada fichero y el texto
  decodificado, así que puede reescribirlos y guardar `encoding` en el nodo File.
- En C#, `SemanticMethodEnricher` solo escribe `docstring` si hay `<summary>`. En csharp-simple no hay comentarios con
  los que comprobar si cbm los estropea.

## Propuesta de tandas

Aprobadas por el usuario el 2026-09-30 las tandas 1 a 4, a ejecutar con subagentes Sonnet y sin más preguntas. La 5
queda para después.

1. **Validador:** leer 02 y 08, citas `:11,16-21`, y pasar `unused_di_registrations` a la plantilla de DI. Sin esto, la
   medida de cualquier mejora sale falseada.
2. **Referencias exactas y campos**, filas 1 y 3, en los dos lenguajes.
3. **`detected_issues` por reglas del compilador**, fila 2, más `auth_required` explícito.
4. **Comentarios**, fila 4, incluida la reescritura de los U+FFFD.
5. **Después:** constantes, nulabilidad (S05), clones y analizadores estándar.

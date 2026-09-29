# Auditoría de configuración de los samples

Fecha: 2026-09-07 · Rama: `consolidation` · Estado: dos correcciones pequeñas aplicadas (sin commitear); el resto son hallazgos, sin tocar.

## 0. Resumen

Hay **un mecanismo correcto** para configurar proveedores/roles LLM en este framework: `AddAITaskAgent()` + `IOptions<LlmEnvironment>`, resuelto por `AITaskAgent.Configuration.ServiceCollectionExtensions.ConfigureLlmEnvironmentOptions`. Valida en el arranque (falla alto y claro si falta `BaseUrl`, si un rol apunta a un provider inexistente, si `ServiceType` no coincide con ningún `ILlmService` registrado) y hace la expansión de `${VAR}` automáticamente.

**5 de 7 samples lo usan correctamente**: `SimpleChatDemo`, `McpIntegrationDemo`, `PipelineVisualizer`, `YamlPipelineDemo`, `FileToolsTestApp` (migrado desde este informe — ver sección 2).

**2 no**: `AdvancedLlmPipeline` y `GeminiTestApp` (este último por un motivo distinto). `AdvancedLlmPipeline` reinventa la hidratación a mano con reflexión, más frágil y sin las validaciones del mecanismo real; lo dice explícitamente en un comentario: *"we don't use BindEx here because it's what fails"* — pero no es un bug del framework: es una entrada `Providers` sin `ServiceType`, y el mecanismo real lanza justo por eso. En vez de añadir una clave al JSON, alguien apagó la validación y la reconstruyó peor, a mano, tres veces.

Aparte: cuatro `appsettings.Development.json` comparten las mismas API keys reales (verificado que ninguna está trackeada en git).

## 1. El mecanismo correcto

`Framework/Configuration/ServiceCollectionExtensions.cs:140-283` (`ConfigureLlmEnvironmentOptions`):
- Lee `AITaskAgent:LlmProviders` vía `section.GetEx<LlmEnvironmentRaw>()` — que expande `${VAR}` en cada valor string automáticamente (`ConfigurationPatchExtensions.cs:185-191`, `ExpandEnvVars`). Ningún sample que use este camino necesita su propio regex de sustitución.
- Por cada `Provider`: `serviceType = r.ServiceType ?? providerName`; busca en `sp.GetServices<ILlmService>()` un servicio cuyo `.Name` coincida (exacto, luego por substring). Si no hay match, **lanza** con la lista de servicios registrados — nunca cae a "el primero que haya".
- Si `BaseUrl` está vacío, **lanza**. Sin excepciones por tipo de servicio.
- Por cada `Role`: si el `Provider` referenciado no existe, **lanza** con la lista de providers disponibles. Si `DefaultRole` no está entre los roles configurados, **lanza**.

Uso típico (`PipelineVisualizer/Program.cs:28-29`, `YamlPipelineDemo/Program.cs:37-50`):
```csharp
services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();
services.AddAITaskAgent();
// ...
var env = sp.GetRequiredService<IOptions<LlmEnvironment>>().Value;
```

Con dos o más backends (`SimpleChatDemo/Program.cs:45-46`, `McpIntegrationDemo/Program.cs:92-97`):
```csharp
services.AddSingleton<ILlmService, GeminiLlmService.GeminiLlmService>();
services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();
```
DI permite registrar la misma interfaz varias veces; `ConfigureLlmEnvironmentOptions` las diferencia por `ServiceType` en el JSON, no por adivinar el nombre del provider.

## 2. Los que no lo usan

### `FileToolsTestApp` — **resuelto (2026-09-09)**
Estado al auditar (2026-09-07): `Program.cs` hacía su propia expansión `${VAR}` (`ReadSettings`, regex idéntica a la del framework), `configSection.Bind(rawEnv)` con `Bind` normal (no `BindEx`, sin expansión, sin validación) y reflexión manual (`ResolveServiceForProvider` por substring del nombre de la clave, y un fallback silencioso al primer provider en `Role → Provider`).

Verificado ahora: `Program.cs` ya usa el mecanismo real. Registra cada proveedor como `ILlmService` explícito (`AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>()`, etc., igual que `SimpleChatDemo`/`McpIntegrationDemo`), llama `AddAITaskAgent()` y resuelve `serviceProvider.GetRequiredService<IOptions<LlmEnvironment>>().Value` — sin `ReadSettings`, sin `Bind` manual, sin reflexión sobre `LlmRole`. No queda hidratación a mano ni fallback silencioso en este sample.

### `AdvancedLlmPipeline`
Mismo patrón, con el comentario explícito del porqué (`Program.cs:116`): *"Note: We don't use BindEx here because it's what fails. We'll do it partially or manually."* Verificado: **no es un fallo del framework**. `ConfigureLlmEnvironmentOptions` exige `ServiceType` (o que la clave del provider coincida literalmente con `.Name` de un `ILlmService`) — el `appsettings.json` de este sample no lo declaraba en ningún provider. `BindEx`/`GetEx` no fallan; lo que falla (correctamente, con mensaje claro) es la validación de after. El arreglo real era una línea de JSON por provider; en su lugar se escribió esto (`Program.cs:126-127`):
```csharp
ILlmService service = (kvp.Key.Contains("Gemini", ...) || kvp.Key.Contains("Google", ...))
    ? geminiService : openAiService;
```
Mismo defecto que mi heurística de `FileToolsTestApp` — coincidencia por texto de la clave, no por `ServiceType`. Y la misma caída silenciosa al provider por defecto si el rol no matchea (`:143-150`).

Adicionalmente, `services.AddSingleton<IOptions<LlmEnvironment>>(new OptionsWrapper<LlmEnvironment>(rawEnv))` (`:163`) sustituye el registro de `AddAITaskAgent()` **después** de haberlo llamado — el comentario lo llama *"Override the broken framework options"*, pero no está roto; es el propio código el que nunca le dio la clave que pedía.

### `GeminiTestApp`
Caso distinto: no usa `LlmEnvironment` en absoluto. Hace `configuration.GetSection("...Providers:Gemini").Get<GeminiSettings>()` con una clase local de solo 4 campos (`Model`, `ApiKey`, `BaseUrl`, `ApiVersion`). Su `appsettings.json` tiene además `"Provider"`, `"EnableThinking"`, `"Temperature"`, `"MaxTokens"`, `"InputTokenPricePerMillion"`, `"OutputTokenPricePerMillion"` — **ninguna se lee nunca**, el binder las descarta en silencio porque `GeminiSettings` no las declara. El sample funciona igual porque no las necesita (razonamiento se activa por nombre de modelo vía `ModelImpliesThinking`, ver sesión anterior; `ApiKey` tiene fallback a la variable de entorno `GEMINI_API_KEY` en el propio código). El propio `SampleConfigurationTests.BindsItsOwnConfig` ya excluye este sample de la comprobación general, así que no es una regresión — es JSON muerto desde que se escribió, y confunde a quien lo lea pensando que esas claves configuran algo.

## 3. Higiene de credenciales

Verificado con `git ls-files`/`git check-ignore`: ninguno de los cuatro `appsettings.Development.json` está trackeado; todos cubiertos por `.gitignore:31` (`**/appsettings.Development.json`). No hay fuga al repositorio.

Pero las mismas claves se repiten, en texto plano, en cuatro archivos:

| Clave (prefijo) | Aparece en |
|---|---|
| `AIzaSyCiKq...` (Gemini) | `GeminiTestApp`, `SimpleChatDemo`, `FileToolsTestApp`, `AdvancedLlmPipeline` |
| `sk-or-v1-3381...` (OpenRouter) | `SimpleChatDemo`, `FileToolsTestApp`, `AdvancedLlmPipeline` |
| `gsk_K90xPm...` (Groq) | `AdvancedLlmPipeline` |

Si estas claves siguen activas, es tuyo decidir si rotarlas — no lo hago sin que lo pidas explícitamente. Lo que sí señalo: cuatro copias de la misma clave en cuatro archivos es una superficie de fuga innecesaria (un zip del repo, un backup, un `tar` que no respete `.gitignore` las expone igual). Una alternativa más barata que tocar el mecanismo de configuración: una única variable de entorno de sesión (`GEMINI_API_KEY`, `OPENROUTER_API_KEY`, `GROQ_API_KEY`) y que **ningún** `appsettings.Development.json` lleve la clave en claro — los cuatro samples ya saben leer `${VAR}` en su archivo base; los `Development.json` con clave literal son redundantes con eso.

## 4. Otras inconsistencias menores

- `YamlPipelineDemo/Program.cs:28` carga `appsettings.development.json` (minúscula) mientras el archivo en disco y el resto de samples usan `appsettings.Development.json`. En Windows no falla (filesystem case-insensitive); en Linux/macOS el overlay nunca cargaría. Cosmético hasta que alguien lo corra fuera de Windows.
- Formato inconsistente entre archivos: casi todos usan 4 espacios de indentación; `AdvancedLlmPipeline/appsettings.Development.json` mezcla tabs (línea 11-13) con espacios.
- `McpIntegrationDemo/appsettings.json` expande `${VAR}` manualmente sobre el texto crudo del JSON (`Program.cs:76-77`) **antes** de construir `IConfiguration`, además de que `AddAITaskAgent()` ya lo haría por su cuenta para `LlmProviders`. No es un bug — cubre también `McpServer:Headers` y cualquier otra sección que no pase por `BindEx` — pero es trabajo duplicado a tener en cuenta si algún día cambia el formato de expansión.

## 5. Corrección propia registrada

Este mismo informe corrige un error de la sesión: al quitar la comprobación de `BaseUrl` en `Tests/SampleConfigurationTests.cs` verifiqué solo que `OpenAILlmService`/`GeminiLlmService` no revientan sin él a nivel de cliente HTTP — cierto, pero irrelevante. El camino real (`ConfigureLlmEnvironmentOptions`, sección 1) sí lo exige, y las 4 muestras bien configuradas ya lo cumplen siempre, incluso para Gemini. La comprobación se restauró; el provider `Gemini` de `FileToolsTestApp` (el único que la incumplía) ganó `BaseUrl` y `ServiceType` explícitos. 181 tests en verde con la comprobación correcta reinstaurada.

## 6. Qué no se ha tocado (pendiente de tu decisión)

1. **Migrar `AdvancedLlmPipeline` al mecanismo real** (`AddAITaskAgent()` + `IOptions<LlmEnvironment>`, `ServiceType` en el JSON de cada provider). Elimina la reflexión manual y el fallback silencioso a "el primer provider". `FileToolsTestApp` ya está migrado (ver sección 2). No afecta a que `AdvancedLlmPipeline` siga invocando `step.ExecuteAsync` directo en vez de `IPipeline` (son decisiones independientes).
2. **Consolidar las credenciales de desarrollo** — variables de entorno de sesión en vez de cuatro copias en claro.
3. **`GeminiTestApp`**: limpiar el JSON muerto o documentar explícitamente que ese archivo no sigue la forma del framework (ya lo sabe `SampleConfigurationTests`, pero no lo dice el propio archivo).
4. Cosméticos de la sección 4, sin urgencia.

# Extensiones propias

Una extensión es la unidad con la que se añade capacidad a una aplicación AITaskAgent: servicios de DI, pasos declarables en YAML o ambos. El LLM (`LlmExtension`), el JavaScript (`ScriptExtension`), las herramientas de ficheros (`FileToolsExtension`), las de Office (`OfficeToolsExtension`) y cada servidor MCP (`McpExtension`) son extensiones que el core no conoce: el core ofrece el contrato y el registro, y la aplicación referencia directamente los ensamblados que quiere y los añade. El framework no carga ensamblados ni paquetes NuGet por sí mismo (eso lo hace, si se quiere, el diseñador de workflows con sus plugins: un paquete NuGet o una carpeta con `plugin.json`, véase `docs/designer/architecture.md` §25; un plugin se lleva con una clase `IExtension` (desde el 2026-10-22; si no tiene ninguna, el diseñador descubre por tipos sus `IStep`, `ITool`/`IToolSource`, hooks por atributo, `ITemplateSource` e `IPluginConfiguration`); en el diseñador su `Register` solo se ejecuta después de que el usuario apruebe el plugin, y `Info()` declara lo que va a registrar, que el cargador compara con lo registrado de verdad), y por eso una extensión es código normal compilado contra `AITaskAgent.Abstractions` (para el contrato) y, si necesita el motor, contra `AITaskAgent.Core`.

## El contrato

```csharp
// namespace AITaskAgent.Core.Abstractions (ensamblado AITaskAgent.Abstractions)
public interface IExtension
{
    string Name { get; }                              // único; una segunda extensión con el mismo nombre se ignora
    void Register(IExtensionBuilder builder);
    // Desde el 2026-10-22, con implementación por defecto:
    // ExtensionInfo Info();   // lo que la extensión va a registrar (vacío por defecto); por descubrimiento (ExtensionDiscovery, AssemblyDiscovery) o a mano
    // IReadOnlyDictionary<string, string?> ConfigurationDefaults(PluginContext context);   // vacío por defecto
    // IReadOnlyList<string> RootSections { get; }   // vacío por defecto
    Type? ConfigurationType => null;                  // clase cuyas propiedades son su configuración, si tiene
    JsonSchema? ConfigurationModel();                 // por defecto, el esquema que se extrae de ConfigurationType
}

public interface IExtensionBuilder
{
    IServiceCollection Services { get; }
    IStepRegistry Steps { get; }                      // el mismo singleton que DI entrega después
    IExtensionBuilder AddStep(Type stepType, string? tag = null);
    IExtensionBuilder AddStep<TStep>(string? tag = null) where TStep : IStep;
    IExtensionBuilder AddStepsFromAssembly(Assembly assembly);
    // Desde el 2026-10-22 también (con cuerpo por defecto que lanza NotSupportedException, salvo Plugin => null):
    // PluginContext? Plugin { get; }
    // AddHook(name, kind, method, description?), AddHooksFromAssembly(assembly), AddResponseType(name, type), AddResponseTypesFromAssembly(assembly),
    // AddToolType(type), AddToolInstance(tool), AddToolSourceInstance(source), AddTemplateSourceInstance(source)
    // y, tipadas, en AITaskAgent.Extensions.Llm: AddTool<T>(), AddTool(ITool), AddToolSource(IToolSource), AddTemplates(ITemplateSource), AddToolsFromAssembly, AddAllFromAssembly.
}
```

`Register` se ejecuta una vez, cuando la aplicación llama a `services.AddExtension(...)`, y recibe la colección de servicios y el registro de pasos de esa colección. Los pasos entran por tag: `AddStep<T>()` usa el `[StepTag]` de la clase (o su nombre de clase si no lo lleva) y `AddStep<T>("Otro")` fija uno explícito; registrar dos tipos distintos bajo el mismo nombre lanza `InvalidOperationException`. `ConfigurationType` apunta a la clase de configuración de la extensión (las opciones o la forma de su sección) y `ConfigurationModel()` extrae su esquema por reflexión de las propiedades públicas con `set` o `init`, sin las marcadas `[JsonIgnore]`: incluye clases anidadas, listas y diccionarios con clave `string`, y marca como obligatorias solo las `[Required]` o `required`. LLM, FileTools y MCP lo declaran; Script y Office no tienen configuración y devuelven `null`. Nada del framework valida con él todavía: sirve para describir la configuración, por ejemplo a un editor.

## Una extensión con un paso y un servicio

El ejemplo registra un paso declarable en YAML (`type: Uppercase`) y el servicio del que depende. El paso es el de [Custom steps](custom-steps.md#declaring-a-step-in-yaml).

```csharp
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Microsoft.Extensions.DependencyInjection;

[StepTag("Uppercase")]
public sealed class UppercaseStep(IGreetingSource? source = null)
    : StepBase("Uppercase", typeof(IStepResult), typeof(StepResult<string>))
{
    public string? Suffix { get; set; }            // configuración YAML: "suffix"

    protected override Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt,
        IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        string text = (input.Value?.ToString() ?? source?.Default ?? "").ToUpperInvariant();
        return Task.FromResult(CreateResult(text + Suffix));
    }
}

public sealed class TextExtension : IExtension
{
    public string Name => "MyCompany.Text";

    public void Register(IExtensionBuilder builder)
    {
        builder.Services.AddSingleton<IGreetingSource, DefaultGreetingSource>();
        builder.AddStep<UppercaseStep>();
    }
}

public static class TextServiceCollectionExtensions
{
    // El azúcar habitual: que el usuario escriba AddTextExtension() y no conozca la clase.
    public static IServiceCollection AddTextExtension(this IServiceCollection services)
        => services.AddExtension(new TextExtension());   // using AITaskAgent.Configuration;
}
```

El registro crea el paso a través de DI, de modo que los parámetros del constructor se resuelven del `IServiceProvider` (los que tienen valor por defecto pueden quedar sin resolver), y después puebla sus propiedades públicas con setter desde el mapa YAML. Si el paso, como `UppercaseStep` aquí, se construye con DI, sus dependencias deben estar registradas en `Register`. Una vez añadida la extensión, el paso se declara así:

```yaml
name: demo
steps:
  - id: shout
    type: Uppercase
    suffix: "!"
```

## Añadirla a la aplicación

`AddExtension` está en `AITaskAgent.Configuration` y se ofrece en dos formas, con instancia (`services.AddExtension(new TextExtension())`) y genérica (`services.AddExtension<TextExtension>()`, para extensiones con constructor sin parámetros). Es idempotente por `Name`: añadir dos veces la misma extensión, o llamar dos veces a `AddFileTools()` o `AddAITaskAgentLlm()`, registra sus servicios y pasos una sola vez (antes de v2 duplicaba los registros). Para extensiones parametrizadas, el nombre debe distinguir las instancias, como hace `McpExtension` con `AITaskAgent.Mcp:{servidor}`; ese caso concreto, además, usa `TryAdd`, y solo el primer servidor MCP de una colección tiene efecto.

El orden importa en un solo caso: `AddExtension` necesita el registro de pasos, que crea `AddAITaskAgentCore()` (o `GetOrAddStepRegistry()`, que `AddExtension` invoca por su cuenta). Si la aplicación registró `IStepRegistry` por tipo o con una fábrica, `GetOrAddStepRegistry` lanza una excepción, porque el registro debe ser una instancia para poder llenarse antes de que exista el proveedor de servicios.

## Reglas para un paso registrable

El registro rechaza con `ArgumentException` los tipos que no implementan `IStep`, los abstractos, las interfaces, los genéricos abiertos y los que no tienen tag, y con `InvalidOperationException` (citando los dos tipos) un tag ya ocupado por otro tipo; registrar el mismo tipo con el mismo tag dos veces no hace nada. Un tag desconocido en `Create` lanza `KeyNotFoundException` con la lista ordenada de tags. De ahí una consecuencia de diseño: `TemplateLlmStep<,>` e `IntentionAnalyzerStep<>` son genéricos abiertos y `RouterStep` exige sus rutas por constructor, así que no son registrables, y `LlmExtension` solo registra el `YamlLlmStep` (tag `LlmStep`). Si un paso necesita variantes tipadas, la forma es un paso concreto no genérico que las configure por propiedades.

Un paso que necesita un contexto concreto (por ejemplo un `PipelineContext` derivado propio) lo declara con `RequiredContext` y el pipeline falla antes de ejecutar nada si no lo recibe; la fábrica de contexto por defecto se sustituye derivando de `PipelineContextFactory` y registrando la derivada, como hace `LlmExtension` con `LlmPipelineContextFactory` (solo reemplaza la del core, no una que la aplicación haya registrado ella misma). Véanse [Custom steps](custom-steps.md#steps-that-need-a-specific-context) y [ARCHITECTURE-v2](../ARCHITECTURE-v2.md).

## Qué no hace una extensión

No tiene `Unregister` ni se descubre sola: la referencia al proyecto y la llamada `AddExtension` son explícitas. No lee `IConfiguration` a través del contrato (`IExtensionBuilder` no la expone); una extensión que necesite configuración la lee de `builder.Services` con opciones, como el resto de servicios (`FileToolsExtension` recibe sus raíces por constructor). Y no recibe notificación de que otra extensión se haya añadido: si depende de servicios de otra, debe documentarlo o comprobarlo en el momento de uso.

## Véase también

- [Custom steps](custom-steps.md): cómo escribir el paso y declararlo en YAML.
- [YAML pipelines](../guides/yaml-pipelines.md): gramática y configuración de los pasos.
- [Extensions](../api-reference/extensions.md): las llamadas de registro, una por una.

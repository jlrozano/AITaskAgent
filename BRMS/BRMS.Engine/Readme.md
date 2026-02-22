# Documentación Técnica: BRMS.Engine

## Información General

| Aspecto | Detalle |
|---------|---------|
| **Nombre del módulo** | BRMS.Engine |
| **Namespace raíz** | BRMS.Engine |
| **Versión** | 1.0.0 |
| **Ubicación** | `/BRMS.Engine` |
| **Propósito general** | Motor principal del sistema BRMS (Business Rules Management System) que proporciona funcionalidades de validación y normalización de datos JSON con soporte para plugins dinámicos. Gestiona esquemas de validación, ejecuta reglas de negocio y procesa datos JSON aplicando transformaciones y validaciones configurables. |
| **Dependencias externas** | Microsoft.AspNetCore.Http.Abstractions (2.3.0), Microsoft.Extensions.Logging (9.0.10), Microsoft.Extensions.Logging.Abstractions (9.0.10), Newtonsoft.Json (13.0.4), NJsonSchema (11.5.1), NJsonSchema.NewtonsoftJson (11.5.1), McMaster.NETCore.Plugins (2.0.0) |
| **Dependencias internas** | BRMS.Abstractions (1.0.6) |

## Índice

1. [Clases](#1-clases)
   - 1.1 [BRMSEngine](#11-clase-brmsengine)
   - 1.2 [SchemaManager](#12-clase-schemamanager)
   - 1.3 [ValidationSchema](#13-clase-validationschema)
   - 1.4 [AppSettingsConfigurationProvider](#14-clase-appsettingsconfigurationprovider)
   - 1.5 [BRMSDI](#15-clase-brmsdi)
2. [Modelos](#2-modelos)
   - 2.1 [ProcessingResult](#21-modelo-processingresult)
   - 2.2 [ProcessingRules](#22-modelo-processingrules)
   - 2.3 [RuleReference](#23-modelo-rulereference)
   - 2.4 [RulesInfo](#24-modelo-rulesinfo)
3. [Inicialización del módulo](#3-inicializacion-del-modulo)
4. [Extensibilidad](#4-extensibilidad)

---

## 1. Clases

### 1.1 Clase BRMSEngine

| Aspecto | Detalle |
|---------|---------|
| **Tipo** | Clase |
| **Namespace** | BRMS.Engine |
| **Ubicación** | /BRMSEngine.cs |
| **Modificadores de acceso** | public |
| **Genericidad** | No genérica |

#### Descripción funcional

**Rol dentro del sistema:** Clase principal del motor BRMS que actúa como punto de entrada central para todas las operaciones de procesamiento de datos JSON. Coordina la carga de plugins, gestión de esquemas y ejecución de reglas de negocio.

**Responsabilidades específicas:**
- Inicialización y configuración del motor BRMS
- Gestión del ciclo de vida de plugins NuGet
- Registro y administración de esquemas de validación
- Procesamiento de datos JSON aplicando reglas de normalización y validación
- Coordinación entre diferentes componentes del sistema

**Cuándo se usa:** Como singleton principal del sistema, se utiliza en toda la aplicación para procesar datos JSON según esquemas y reglas definidas.

**Cómo se integra:** Se registra como singleton en el contenedor de dependencias y se accede a través de la propiedad estática Engine o mediante inyección de dependencias.

**Patrones de diseño aplicados:** Singleton Pattern, Factory Pattern (para creación de instancias), Facade Pattern (simplifica el acceso a subsistemas complejos).

#### Hereda de:
- Ninguna

#### Es heredada por:
- Ninguna (clase sellada implícitamente)

#### Relaciones con otras entidades:
- Usa: [SchemaManager](#12-clase-schemamanager), NuGetPackageLoader, ILogger
- Usada por: [BRMSDI](#15-clase-brmsdi), aplicaciones cliente

#### Propiedades

| Nombre | Tipo | Descripción | Propósito | Valores por defecto |
|--------|------|-------------|-----------|---------------------|
| Engine | BRMSEngine (static) | Instancia singleton del motor BRMS | Acceso global al motor inicializado | null (lanza excepción si no está inicializado) |

#### Métodos

| Nombre | Descripción | Parámetros | Retorno | Excepciones |
|--------|-------------|------------|---------|-------------|
| Create | Crea e inicializa una nueva instancia del motor BRMS de forma asíncrona | - serviceCollection: IServiceCollection - Colección de servicios<br>- nugets: NuGetLoaderConfig? - Configuración de paquetes NuGet<br>- logger: ILogger - Logger para registro de eventos | Task<BRMSEngine> - Instancia inicializada del motor | - Exception: Error durante la inicialización |
| RegisterPlugins | Registra todos los plugins cargados en el proveedor de servicios | - serviceProvider: IServiceProvider - Proveedor de servicios | Task - Operación asíncrona | - Exception: Error durante el registro de plugins |
| RegisterSchema | Registra un esquema de validación en el sistema | **Overload 1:**<br>- name: string - Nombre del esquema<br>- schema: ValidationSchema - Esquema a registrar<br><br>**Overload 2:**<br>- name: string - Nombre del esquema<br>- schema: ValidationSchema - Esquema a registrar<br>- errors: out List<string> - Lista de errores encontrados | bool - true si el registro fue exitoso | - ArgumentException: Si el nombre está vacío<br>- ArgumentNullException: Si schema es null |
| UnRegisterSchema | Desregistra un esquema del sistema | - name: string - Nombre del esquema a desregistrar | void | Ninguna |
| ProcessJson | Procesa datos JSON aplicando las reglas del esquema especificado | - schemaName: string - Nombre del esquema<br>- jsonData: string - Datos JSON a procesar<br>- cancellationToken: CancellationToken (opcional) - Token de cancelación | Task<ProcessingResult> - Resultado del procesamiento | - ArgumentException: Si schemaName está vacío<br>- ArgumentNullException: Si jsonData es null<br>- KeyNotFoundException: Si el esquema no existe<br>- JsonException: Si el JSON es inválido |
| GetAvailableRules | Obtiene información sobre las reglas disponibles en el sistema | Ninguno | RulesInfo - Información de reglas disponibles | Ninguna |
| ValidateValueWithSchema | Valida un valor contra un esquema JSON específico | - value: JToken - Valor a validar<br>- schema: JsonSchema - Esquema de validación<br>- errors: List<string> - Lista para almacenar errores<br>- propertyPath: string - Ruta de la propiedad | bool - true si la validación es exitosa | Ninguna |

#### Consideraciones de concurrencia y rendimiento

| Aspecto | Detalle |
|---------|---------|
| **Thread-safety** | Sí - Utiliza Lock para sincronización durante la inicialización |
| **Inmutabilidad** | Parcial - La instancia es inmutable después de la inicialización |
| **Recomendaciones multi-hilo** | Seguro para uso concurrente después de la inicialización |
| **Consideraciones de rendimiento** | Singleton con inicialización lazy, caching de esquemas compilados |
| **Uso de recursos** | Mantiene plugins cargados en memoria, gestiona conexiones a repositorios NuGet |
| **Disposable** | No implementa IDisposable directamente |

#### Observaciones técnicas

**Patrón Singleton con inicialización thread-safe:**

El método `Create` implementa un patrón singleton con doble verificación de bloqueo para garantizar que solo se cree una instancia del motor, incluso en entornos multi-hilo:

1. **Primera verificación sin bloqueo**: Verifica si `_engine` es null antes de adquirir el lock
2. **Bloqueo exclusivo**: Utiliza `lock (_lock)` para sincronizar el acceso
3. **Segunda verificación dentro del bloqueo**: Usa el operador null-coalescing `??=` para asignar solo si es null
4. **Inicialización asíncrona**: El constructor privado puede realizar operaciones asíncronas de carga de plugins

**Gestión de plugins dinámicos:**

El motor utiliza `NuGetPackageLoader` para cargar plugins dinámicamente:

1. **Descarga de paquetes**: Descarga paquetes NuGet especificados en la configuración
2. **Carga de assemblies**: Utiliza McMaster.NETCore.Plugins para cargar assemblies de forma aislada
3. **Descubrimiento de tipos**: Busca implementaciones de `IPlugin` en los assemblies cargados
4. **Registro automático**: Invoca el método `Register` de cada plugin encontrado

**Procesamiento de JSON con pipeline de reglas:**

El método `ProcessJson` implementa un pipeline de procesamiento en múltiples fases:

1. **Validación de esquema**: Valida el JSON contra el esquema JsonSchema definido
2. **Aplicación de normalizadores**: Ejecuta reglas de normalización en el orden especificado
3. **Aplicación de validadores**: Ejecuta reglas de validación sobre los datos normalizados
4. **Agregación de resultados**: Combina todos los resultados en un objeto `ProcessingResult`

### 1.2 Clase SchemaManager

| Aspecto | Detalle |
|---------|---------|
| **Tipo** | Clase |
| **Namespace** | BRMS.Engine |
| **Ubicación** | /SchemaManager.cs |
| **Modificadores de acceso** | internal |
| **Genericidad** | No genérica |

#### Descripción funcional

**Rol dentro del sistema:** Administrador interno de esquemas de validación que mantiene un registro de todos los esquemas disponibles y proporciona operaciones CRUD sobre ellos.

**Responsabilidades específicas:**
- Almacenar y organizar esquemas de validación por nombre
- Validar la integridad de esquemas antes del registro
- Proporcionar acceso thread-safe a los esquemas
- Mantener registro de errores de configuración

**Cuándo se usa:** Internamente por BRMSEngine para gestionar el ciclo de vida de los esquemas de validación.

**Cómo se integra:** Utilizado exclusivamente por BRMSEngine como componente interno de gestión de esquemas.

#### Hereda de:
- Ninguna

#### Es heredada por:
- Ninguna

#### Relaciones con otras entidades:
- Usa: [ValidationSchema](#13-clase-validationschema), NuGetLoaderConfig
- Usada por: [BRMSEngine](#11-clase-brmsengine)

#### Propiedades

| Nombre | Tipo | Descripción | Propósito | Valores por defecto |
|--------|------|-------------|-----------|---------------------|
| NuGetLoaderConfig | NuGetLoaderConfig? | Configuración del cargador de paquetes NuGet | Almacenar configuración de plugins | null |
| Errors | List<string> | Lista de errores de configuración | Registro de problemas durante operaciones | Lista vacía |

#### Métodos

| Nombre | Descripción | Parámetros | Retorno | Excepciones |
|--------|-------------|------------|---------|-------------|
| AddSchema | Añade un nuevo esquema al registro | - name: string - Nombre único del esquema<br>- schema: ValidationSchema - Esquema a añadir | bool - true si se añadió exitosamente | - ArgumentException: Si el nombre ya existe |
| GetSchema | Obtiene un esquema por su nombre | - name: string - Nombre del esquema | ValidationSchema? - Esquema encontrado o null | Ninguna |
| RemoveSchema | Elimina un esquema del registro | - name: string - Nombre del esquema a eliminar | bool - true si se eliminó exitosamente | Ninguna |
| ValidateSchema | Valida la configuración de un esquema | - schema: ValidationSchema - Esquema a validar | List<string> - Lista de errores encontrados | Ninguna |

### 1.3 Clase ValidationSchema

| Aspecto | Detalle |
|---------|---------|
| **Tipo** | Clase |
| **Namespace** | BRMS.Engine |
| **Ubicación** | /ValidationSchema.cs |
| **Modificadores de acceso** | public |
| **Genericidad** | No genérica |

#### Descripción funcional

**Rol dentro del sistema:** Representa un esquema de validación completo que combina un esquema JSON con una lista de reglas de procesamiento (normalizadores y validadores).

**Responsabilidades específicas:**
- Mantener la definición del esquema JSON para validación estructural
- Gestionar la lista de reglas a aplicar durante el procesamiento
- Compilar y preparar las reglas para ejecución
- Proporcionar información sobre errores de compilación

**Cuándo se usa:** Para definir esquemas completos de validación que incluyen tanto validación estructural como reglas de negocio específicas.

**Cómo se integra:** Utilizada por SchemaManager y BRMSEngine para definir y ejecutar esquemas de validación completos.

#### Hereda de:
- Ninguna

#### Es heredada por:
- Ninguna

#### Relaciones con otras entidades:
- Usa: JsonSchema, [ProcessingRules](#22-modelo-processingrules), RuleManager
- Usada por: [SchemaManager](#12-clase-schemamanager), [BRMSEngine](#11-clase-brmsengine)

#### Propiedades

| Nombre | Tipo | Descripción | Propósito | Valores por defecto |
|--------|------|-------------|-----------|---------------------|
| DataSchema | JsonSchema | Esquema JSON para validación estructural | Definir la estructura esperada de los datos | null |
| Rules | ProcessingRules | Lista de reglas a aplicar en orden | Especificar normalizadores y validadores | null |
| CompiledRules | List<IRule> | Lista de reglas construidas y listas para usar | Almacenar instancias compiladas de reglas | Lista vacía |
| Errors | List<string> | Errores encontrados durante la compilación | Registro de problemas de configuración | Lista vacía |

#### Métodos

| Nombre | Descripción | Parámetros | Retorno | Excepciones |
|--------|-------------|------------|---------|-------------|
| Build | Construye las reglas del esquema usando el RuleManager | Ninguno | List<string> - Lista de errores encontrados durante la construcción | - Exception: Error durante la construcción de reglas |

### 1.4 Clase AppSettingsConfigurationProvider

| Aspecto | Detalle |
|---------|---------|
| **Tipo** | Clase |
| **Namespace** | BRMS.Engine |
| **Ubicación** | /AppSettingsConfigurationProvider.cs |
| **Modificadores de acceso** | public |
| **Genericidad** | No genérica |

#### Descripción funcional

**Rol dentro del sistema:** Proveedor de configuración que carga y gestiona archivos appsettings.json y sus variantes específicas de entorno, implementando la interfaz IBRMSConfigurationProvider.

**Responsabilidades específicas:**
- Cargar archivos de configuración JSON (appsettings.json, appsettings.{Environment}.json)
- Fusionar configuraciones de múltiples archivos
- Proporcionar acceso a valores de configuración mediante JSONPath
- Deserializar configuraciones a tipos específicos

**Cuándo se usa:** Para acceder a configuraciones de la aplicación desde archivos JSON estándar de .NET.

**Cómo se integra:** Implementa IBRMSConfigurationProvider y puede ser utilizada por cualquier componente que necesite acceso a configuración.

#### Hereda de:
- Ninguna

#### Implementa:
- IBRMSConfigurationProvider

#### Es heredada por:
- Ninguna

#### Relaciones con otras entidades:
- Implementa: IBRMSConfigurationProvider
- Usa: ILogger, Newtonsoft.Json

#### Propiedades

| Nombre | Tipo | Descripción | Propósito | Valores por defecto |
|--------|------|-------------|-----------|---------------------|
| Configuration | JObject | Configuración JSON fusionada | Almacenar toda la configuración cargada | JObject vacío |

#### Constructores

| Constructor | Parámetros | Descripción | Cuándo usar |
|-------------|------------|-------------|-------------|
| AppSettingsConfigurationProvider | - logger: ILogger - Logger para registro de eventos<br>- environment: string? (opcional) - Nombre del entorno | Inicializa el proveedor y carga los archivos de configuración | Para crear una instancia del proveedor de configuración |

#### Métodos

| Nombre | Descripción | Parámetros | Retorno | Excepciones |
|--------|-------------|------------|---------|-------------|
| GetValueAsync | Obtiene un valor de configuración como tipo de valor | - key: string - Clave JSONPath de la configuración | Task<T?> - Valor deserializado o default(T) | - JsonException: Error de deserialización |
| GetObjectAsync | Obtiene un objeto de configuración como tipo de referencia | - key: string - Clave JSONPath de la configuración | Task<T?> - Objeto deserializado o null | - JsonException: Error de deserialización |

### 1.5 Clase BRMSDI

| Aspecto | Detalle |
|---------|---------|
| **Tipo** | Clase estática |
| **Namespace** | BRMS.Engine |
| **Ubicación** | /BRMSEngine.cs |
| **Modificadores de acceso** | public |
| **Genericidad** | No genérica |

#### Descripción funcional

**Rol dentro del sistema:** Clase de extensión estática que proporciona métodos de extensión para integrar el motor BRMS en el pipeline de inyección de dependencias de ASP.NET Core.

**Responsabilidades específicas:**
- Registrar el motor BRMS como singleton en el contenedor de servicios
- Configurar el proveedor de servicios en RuleManager
- Simplificar la configuración inicial del sistema

**Cuándo se usa:** Durante la configuración inicial de la aplicación en Program.cs o Startup.cs.

**Cómo se integra:** Proporciona métodos de extensión para IServiceCollection e IApplicationBuilder.

**Patrones de diseño aplicados:** Extension Methods Pattern, Builder Pattern (para configuración fluida).

#### Hereda de:
- Ninguna (clase estática)

#### Es heredada por:
- Ninguna (clase estática)

#### Relaciones con otras entidades:
- Usa: [BRMSEngine](#11-clase-brmsengine), IServiceCollection, IApplicationBuilder, RuleManager
- Usada por: Aplicaciones ASP.NET Core

#### Métodos

| Nombre | Descripción | Parámetros | Retorno | Excepciones |
|--------|-------------|------------|---------|-------------|
| AddBrmsEngine | Registra el motor BRMS como singleton en el contenedor de servicios | - sc: IServiceCollection - Colección de servicios<br>- nugets: NuGetLoaderConfig? - Configuración de paquetes NuGet<br>- logger: ILogger - Logger para el motor | Task - Operación asíncrona | - Exception: Error durante la creación del motor |
| UseBrmsEngine | Configura el motor BRMS en el pipeline de la aplicación | - app: IApplicationBuilder - Constructor de aplicación | Task<IApplicationBuilder> - Constructor de aplicación configurado | - Exception: Error durante la configuración |

---

## 2. Modelos

### 2.1 Modelo ProcessingResult

| Aspecto | Detalle |
|---------|---------|
| **Tipo** | Record |
| **Namespace** | BRMS.Engine |
| **Ubicación** | /ProcessingResult.cs |
| **Modificadores de acceso** | public |
| **Genericidad** | No genérica |

#### Descripción funcional

**Rol dentro del sistema:** Modelo que encapsula el resultado completo del procesamiento de datos JSON, incluyendo el estado de éxito, datos procesados y todos los resultados de reglas aplicadas.

**Responsabilidades específicas:**
- Indicar si el procesamiento fue exitoso
- Contener los datos JSON procesados
- Almacenar resultados de normalización y validación
- Registrar errores y excepciones ocurridas

**Cuándo se usa:** Como valor de retorno del método ProcessJson de BRMSEngine.

**Cómo se integra:** Utilizado por aplicaciones cliente para evaluar el resultado del procesamiento de datos.

#### Hereda de:
- Ninguna

#### Es heredada por:
- Ninguna

#### Propiedades

| Nombre | Tipo | Descripción | Propósito | Valores por defecto |
|--------|------|-------------|-----------|---------------------|
| Success | bool | Indica si el procesamiento fue exitoso | Determinar el estado general del procesamiento | false |
| ProcessedJson | string? | Datos JSON procesados | Contener el resultado final del procesamiento | null |
| NormalizationResults | List<INormalizerResult> | Resultados de la aplicación de normalizadores | Registrar cambios realizados por normalizadores | Lista vacía |
| RuleResults | List<IRuleResult> | Resultados de la aplicación de validadores | Registrar resultados de validaciones | Lista vacía |
| SchemaValidationResults | List<string> | Resultados de la validación del esquema JSON | Registrar errores de validación estructural | Lista vacía |
| ProcessingErrors | List<string> | Errores encontrados durante el procesamiento | Registrar problemas no críticos | Lista vacía |
| Exception | Exception? | Excepción crítica si ocurrió | Registrar errores críticos | null |

### 2.2 Modelo ProcessingRules

| Aspecto | Detalle |
|---------|---------|
| **Tipo** | Record |
| **Namespace** | BRMS.Engine |
| **Ubicación** | /ProcessingRules.cs |
| **Modificadores de acceso** | public |
| **Genericidad** | No genérica |

#### Descripción funcional

**Rol dentro del sistema:** Modelo que define el orden y configuración de las reglas a aplicar durante el procesamiento de datos JSON.

**Responsabilidades específicas:**
- Especificar la secuencia de normalizadores a aplicar
- Definir la secuencia de validadores a ejecutar
- Mantener el orden de ejecución de las reglas

**Cuándo se usa:** Para configurar esquemas de validación que requieren múltiples reglas en un orden específico.

**Cómo se integra:** Utilizado por ValidationSchema para definir las reglas de procesamiento.

#### Hereda de:
- Ninguna

#### Es heredada por:
- Ninguna

#### Propiedades

| Nombre | Tipo | Descripción | Propósito | Valores por defecto |
|--------|------|-------------|-----------|---------------------|
| Normalizers | List<RuleReference> | Normalizadores a aplicar en orden | Definir transformaciones de datos | Lista vacía |
| Validators | List<RuleReference> | Validadores a aplicar en orden | Definir validaciones de negocio | Lista vacía |

### 2.3 Modelo RuleReference

| Aspecto | Detalle |
|---------|---------|
| **Tipo** | Record |
| **Namespace** | BRMS.Engine |
| **Ubicación** | /RuleReference.cs |
| **Modificadores de acceso** | public |
| **Genericidad** | No genérica |

#### Descripción funcional

**Rol dentro del sistema:** Modelo que representa una referencia a una regla específica con su configuración asociada.

**Responsabilidades específicas:**
- Identificar una regla por su nombre
- Proporcionar configuración JSON específica para la regla
- Servir como enlace entre esquemas y implementaciones de reglas

**Cuándo se usa:** Para referenciar reglas específicas dentro de ProcessingRules con configuración personalizada.

**Cómo se integra:** Utilizado por ProcessingRules para especificar qué reglas aplicar y cómo configurarlas.

#### Hereda de:
- Ninguna

#### Es heredada por:
- Ninguna

#### Propiedades

| Nombre | Tipo | Descripción | Propósito | Valores por defecto |
|--------|------|-------------|-----------|---------------------|
| RuleName | string | Nombre de la regla a aplicar | Identificar la regla en RuleManager | string.Empty |
| Configuration | JObject? | Configuración JSON opcional para la regla | Parametrizar el comportamiento de la regla | null |

### 2.4 Modelo RulesInfo

| Aspecto | Detalle |
|---------|---------|
| **Tipo** | Record |
| **Namespace** | BRMS.Engine |
| **Ubicación** | /RulesInfo.cs |
| **Modificadores de acceso** | public |
| **Genericidad** | No genérica |

#### Descripción funcional

**Rol dentro del sistema:** Modelo que proporciona información estadística y descriptiva sobre las reglas disponibles en el sistema.

**Responsabilidades específicas:**
- Reportar el número total de reglas registradas
- Proporcionar descripciones de validadores disponibles
- Listar normalizadores disponibles
- Informar sobre el estado de carga de plugins

**Cuándo se usa:** Para obtener información sobre el estado actual del sistema de reglas, útil para diagnósticos y interfaces de administración.

**Cómo se integra:** Devuelto por el método GetAvailableRules de BRMSEngine.

#### Hereda de:
- Ninguna

#### Es heredada por:
- Ninguna

#### Propiedades

| Nombre | Tipo | Descripción | Propósito | Valores por defecto |
|--------|------|-------------|-----------|---------------------|
| TotalRules | int | Número total de reglas registradas | Proporcionar estadísticas del sistema | 0 |
| Validators | List<RuleDescription> | Descripciones de validadores disponibles | Listar validadores con sus metadatos | Lista vacía |
| Normalizers | List<RuleDescription> | Descripciones de normalizadores disponibles | Listar normalizadores con sus metadatos | Lista vacía |
| LoadedPluginsCount | int | Número de plugins cargados exitosamente | Indicar estado de carga de plugins | 0 |
| PluginLoadingErrors | List<string> | Errores ocurridos durante la carga de plugins | Registrar problemas de carga | Lista vacía |

---

## 3. Inicialización del módulo

### Registro en ASP.NET Core

El módulo BRMS.Engine se integra en aplicaciones ASP.NET Core mediante métodos de extensión que simplifican la configuración:

```csharp
// En Program.cs o Startup.cs
var builder = WebApplication.CreateBuilder(args);

// Registrar el motor BRMS
await builder.Services.AddBrmsEngine(
    nugets: new NuGetLoaderConfig 
    { 
        PackageSources = ["https://api.nuget.org/v3/index.json"],
        Packages = ["MiPaqueteDeReglas.1.0.0"]
    },
    logger: builder.Services.BuildServiceProvider().GetRequiredService<ILogger<BRMSEngine>>()
);

var app = builder.Build();

// Configurar el motor en el pipeline
await app.UseBrmsEngine();
```

### Orden de inicialización

1. **Creación del motor**: Se crea la instancia singleton de BRMSEngine
2. **Carga de plugins**: Se descargan y cargan los paquetes NuGet especificados
3. **Registro de reglas**: Se registran automáticamente todas las reglas encontradas en los plugins
4. **Configuración del proveedor de servicios**: Se establece el ServiceProvider en RuleManager
5. **Disponibilidad del motor**: El motor queda disponible para procesamiento

### Configuración requerida

#### Configuración de plugins (opcional)
```json
{
  "BRMS": {
    "NuGetConfig": {
      "PackageSources": [
        "https://api.nuget.org/v3/index.json",
        "https://mi-feed-privado.com/nuget"
      ],
      "Packages": [
        "MiPaqueteValidadores.1.2.0",
        "MiPaqueteNormalizadores.2.1.0"
      ]
    }
  }
}
```

#### Configuración de logging
El motor requiere un ILogger para registrar eventos importantes:
- Carga de plugins
- Errores de procesamiento
- Información de rendimiento

---

## 4. Extensibilidad

### Creación de plugins personalizados

Para extender el motor con reglas personalizadas, crear un paquete NuGet que implemente IPlugin:

```csharp
public class MiPlugin : Plugin
{
    public override async Task Register()
    {
        // Registrar validadores personalizados
        RuleManager.AddRule<IValidator>(() => new MiValidadorPersonalizado(), "MiValidador");
        
        // Registrar normalizadores personalizados
        RuleManager.AddRule<INormalizer>(() => new MiNormalizadorPersonalizado(), "MiNormalizador");
    }
}
```

### Implementación de reglas personalizadas

#### Validador personalizado
```csharp
[RuleName("ValidadorEmail")]
public class ValidadorEmail : Validator
{
    public override async Task<IRuleResult> Invoke(BRMSExecutionContext context, CancellationToken cancellationToken = default)
    {
        var email = context.CurrentValue?.ToString();
        var isValid = IsValidEmail(email);
        
        return new RuleResult
        {
            Success = isValid,
            Message = isValid ? "Email válido" : "Formato de email inválido",
            PropertyPath = PropertyPath
        };
    }
    
    private bool IsValidEmail(string email) => /* lógica de validación */;
}
```

#### Normalizador personalizado
```csharp
[RuleName("NormalizadorTexto")]
public class NormalizadorTexto : Normalizer
{
    public override async Task<INormalizerResult> Invoke(BRMSExecutionContext context, CancellationToken cancellationToken = default)
    {
        var texto = context.CurrentValue?.ToString();
        var textoNormalizado = texto?.Trim().ToLowerInvariant();
        
        return new NormalizerResult
        {
            Success = true,
            OriginalValue = context.CurrentValue,
            NormalizedValue = JToken.FromObject(textoNormalizado),
            PropertyPath = PropertyPath
        };
    }
}
```

### Configuración de esquemas

Los esquemas se pueden definir programáticamente o cargar desde configuración:

```csharp
var schema = new ValidationSchema
{
    DataSchema = JsonSchema.FromType<MiModelo>(),
    Rules = new ProcessingRules
    {
        Normalizers = 
        [
            new RuleReference { RuleName = "NormalizadorTexto" }
        ],
        Validators = 
        [
            new RuleReference 
            { 
                RuleName = "ValidadorEmail",
                Configuration = JObject.FromObject(new { Domain = "miempresa.com" })
            }
        ]
    }
};

BRMSEngine.Engine.RegisterSchema("MiEsquema", schema);
```

### Puntos de extensión

- **IPlugin**: Para crear paquetes de reglas distribuibles
- **IValidator**: Para validaciones de negocio personalizadas
- **INormalizer**: Para transformaciones de datos personalizadas
- **IBRMSConfigurationProvider**: Para proveedores de configuración personalizados
- **Atributos personalizados**: Para metadatos adicionales en reglas

---

**Versión de la documentación:** 1.0  
**Última actualización:** 2025-01-27  
**Versión del código documentada:** 1.0.0
namespace BRMS.Core.Constants;

/// <summary>
/// Keys for all constant values in the BRMS system.
/// </summary>
public enum BrmsConstantKeys
{
    // DefaultMessages
    DefaultMessages_InvalidConfiguration,
    DefaultMessages_RuleNotFound,

    // ErrorMessages
    ErrorMessages_RuleNameCannotBeEmpty,
    ErrorMessages_TypeMustImplementIRule,
    ErrorMessages_RuleNotFound,

    // LogMessages
    LogMessages_RuleRegistered,
    LogMessages_ExecutingRule,
    LogMessages_RuleExecuted,
    LogMessages_RuleExecutionFailed,
    LogMessages_RuleNotFound,
    LogMessages_RuleUnregistered,
    LogMessages_AllRulesCleared,
    LogMessages_ExecutingMultipleRules,
    LogMessages_RuleExecutionCancelled,
    LogMessages_ExecutingAsyncRule,
    LogMessages_AsyncRuleCompleted,
    LogMessages_AsyncRuleCancelled,
    LogMessages_AsyncRuleFailed,
    LogMessages_NugetRepositoriesFound,
    LogMessages_NoNupkgPackagesAvailable,
    LogMessages_NugetLocalDirectoryNotExists,
    LogMessages_PackageNotFoundInRepositories,
    LogMessages_PackageDownloadFailed,
    LogMessages_NoCompatibleFrameworkFound,
    LogMessages_NoDllsFoundInPackage,
    LogMessages_PluginCreationWithDiFailed,
    LogMessages_ServiceCollectionNotAvailable,
    LogMessages_PluginRulesRegisteredSuccessfully,
    LogMessages_PluginRulesRegistrationFailed,

    // ConfigurationSections
    ConfigurationSections_Brms,

    // Paths
    Paths_BrmsNugetConfigEnv,
    Paths_NugetConfig,
    Paths_NugetLocal,
    Paths_LocalNugetPath,  // Nueva constante
    Paths_NupkgPattern,

    // NetFrameworks
    NetFrameworks_NetCoreApp,
    NetFrameworks_Net,
    NetFrameworks_DllExtension,

    // SchemaFormats
    SchemaFormats_DateTimeFormat,
    SchemaFormats_DateTimeDescription,
    SchemaFormats_DateFormat,
    SchemaFormats_DateDescription,
    SchemaFormats_EmailFormat,
    SchemaFormats_PhoneFormat,
    SchemaFormats_UriFormat,
    SchemaFormats_ValidValuesTemplate,
    SchemaFormats_ValueSeparator,
    SchemaFormats_DescriptionSeparator,
    SchemaFormats_MinimumDateTemplate,
    SchemaFormats_MaximumDateTemplate,
    SchemaFormats_LengthTemplate,
    SchemaFormats_MinimumLengthTemplate,
    SchemaFormats_MaximumLengthTemplate,
    SchemaFormats_MinimumItemsTemplate,
    SchemaFormats_MaximumItemsTemplate,

    // ErrorCodes
    ErrorCodes_ValidationFailed,

    // Timeouts
    Timeouts_DefaultValidation,

    // JsonPath
    JsonPath_Root
}

/// <summary>
/// Contains all constant values used throughout the BRMS system.
/// All strings are in English for consistency.
/// </summary>
internal static class BrmsConstants
{


    /// <summary>
    /// Default error and informational messages.
    /// </summary>
    public static class DefaultMessages
    {
        public static string InvalidConfiguration => ResourcesManager.GetLocalizedMessage("DefaultMessages_InvalidConfiguration") ?? "Invalid configuration provided";
        public static string RuleNotFound => ResourcesManager.GetLocalizedMessage("DefaultMessages_RuleNotFound") ?? "Rule not found";
    }

    /// <summary>
    /// Error messages used throughout the BRMS system
    /// </summary>
    public static class ErrorMessages
    {
        public static string RuleNameCannotBeEmpty => ResourcesManager.GetLocalizedMessage("ErrorMessages_RuleNameCannotBeEmpty") ?? "Rule name cannot be null or empty";
        public static string TypeMustImplementIRule => ResourcesManager.GetLocalizedMessage("ErrorMessages_TypeMustImplementIRule") ?? "Type must implement IRule interface";
        public static string RuleNotFoundInRegistry => ResourcesManager.GetLocalizedMessage("ErrorMessages_RuleNotFound") ?? "Rule '{0}' not found in the registry";
        public static string JsonPathMustStartWithDollar => ResourcesManager.GetLocalizedMessage("ErrorMessages_JsonPathMustStartWithDollar") ?? "JSON path must start with '$'. Provided: '{0}'";
        public static string PropertyNotFoundOrNotReadable => ResourcesManager.GetLocalizedMessage("ErrorMessages_PropertyNotFoundOrNotReadable") ?? "Property '{0}' not found or not readable on type '{1}'.";
        public static string PropertyNotFoundOrNotWritable => ResourcesManager.GetLocalizedMessage("ErrorMessages_PropertyNotFoundOrNotWritable") ?? "Property '{0}' not found or not writable on type '{1}'.";
        public static string TypeDoesNotImplementIRule => ResourcesManager.GetLocalizedMessage("ErrorMessages_TypeDoesNotImplementIRule") ?? "Type {0} does not implement IRule";
        public static string UnableToCreateRuleInstance => ResourcesManager.GetLocalizedMessage("ErrorMessages_UnableToCreateRuleInstance") ?? "Unable to create instance of rule type {0}";
        public static string RuleTypeDoesNotImplementIRule => ResourcesManager.GetLocalizedMessage("ErrorMessages_RuleTypeDoesNotImplementIRule") ?? "Rule type '{0}' does not implement IRule interface.";
        public static string PluginLoadFailed => ResourcesManager.GetLocalizedMessage("ErrorMessages_PluginLoadFailed") ?? "No se pudo cargar el plugin {0}. Paquetes disponibles en nuget-local: {1}";
        public static string PluginLoadError => ResourcesManager.GetLocalizedMessage("ErrorMessages_PluginLoadError") ?? "Error cargando plugin {0}: {1}";
        public static string PluginLoadGenericError => ResourcesManager.GetLocalizedMessage("ErrorMessages_PluginLoadGenericError") ?? "Error cargando el plugin {0}: {1}";
    }

    /// <summary>
    /// Structured logging message templates.
    /// </summary>
    public static class LogMessages
    {
        // Rule execution messages
        public static string RuleRegistered => ResourcesManager.GetLocalizedMessage("LogMessages_RuleRegistered") ?? "Regla registrada: {RuleName} de tipo {RuleType}";
        public static string RuleExecuted => ResourcesManager.GetLocalizedMessage("LogMessages_RuleExecuted") ?? "Regla ejecutada: {RuleName}, Éxito: {IsSuccess}";
        public static string RuleExecutionFailed => ResourcesManager.GetLocalizedMessage("LogMessages_RuleExecutionFailed") ?? "Ejecución de regla fallida: {RuleName}";
        public static string RuleNotFoundInLog => ResourcesManager.GetLocalizedMessage("LogMessages_RuleNotFound") ?? "Regla no encontrada: {RuleName}";
        public static string RuleUnregistered => ResourcesManager.GetLocalizedMessage("LogMessages_RuleUnregistered") ?? "Regla desregistrada: {RuleName}";
        public static string AllRulesCleared => ResourcesManager.GetLocalizedMessage("LogMessages_AllRulesCleared") ?? "Todas las reglas limpiadas. Cantidad: {Count}";
        public static string ExecutingMultipleRules => ResourcesManager.GetLocalizedMessage("LogMessages_ExecutingMultipleRules") ?? "Ejecutando múltiples reglas. Cantidad: {Count}";
        public static string RuleExecutionCancelled => ResourcesManager.GetLocalizedMessage("LogMessages_RuleExecutionCancelled") ?? "Ejecución de regla cancelada: {RuleName}";

        // Async rule messages
        public static string ExecutingAsyncRule => ResourcesManager.GetLocalizedMessage("LogMessages_ExecutingAsyncRule") ?? "Ejecutando regla asíncrona: {RuleType} con contexto: {ContextType}";
        public static string AsyncRuleCompleted => ResourcesManager.GetLocalizedMessage("LogMessages_AsyncRuleCompleted") ?? "Regla asíncrona completada: {RuleType} en {ExecutionTime} ms";
        public static string AsyncRuleCancelled => ResourcesManager.GetLocalizedMessage("LogMessages_AsyncRuleCancelled") ?? "Regla asíncrona cancelada: {RuleType}";
        public static string AsyncRuleFailed => ResourcesManager.GetLocalizedMessage("LogMessages_AsyncRuleFailed") ?? "Regla asíncrona fallida: {RuleType} - {ErrorMessage}";

        // Plugin loading messages
        public static string NugetRepositoriesFound => ResourcesManager.GetLocalizedMessage("LogMessages_NugetRepositoriesFound") ?? "Repositorios NuGet encontrados: {Repositories}";
        public static string NoNupkgPackagesAvailable => ResourcesManager.GetLocalizedMessage("LogMessages_NoNupkgPackagesAvailable") ?? "No hay paquetes .nupkg disponibles en nuget-local";
        public static string NugetLocalDirectoryNotExists => ResourcesManager.GetLocalizedMessage("LogMessages_NugetLocalDirectoryNotExists") ?? "El directorio nuget-local no existe";
        public static string PackageNotFoundInRepositories => ResourcesManager.GetLocalizedMessage("LogMessages_PackageNotFoundInRepositories") ?? "Paquete no encontrado en ningún repositorio: {PackageId}";
        public static string PackageDownloadFailed => ResourcesManager.GetLocalizedMessage("LogMessages_PackageDownloadFailed") ?? "Fallo al descargar paquete: {PackageId}";
        public static string NoCompatibleFrameworkFound => ResourcesManager.GetLocalizedMessage("LogMessages_NoCompatibleFrameworkFound") ?? "No se encontró framework compatible para el paquete: {PackageId}";
        public static string NoDllsFoundInPackage => ResourcesManager.GetLocalizedMessage("LogMessages_NoDllsFoundInPackage") ?? "No se encontraron archivos DLL en el paquete: {PackageId}";
        public static string PluginCreationWithDiFailed => ResourcesManager.GetLocalizedMessage("LogMessages_PluginCreationWithDiFailed") ?? "Creación de plugin con DI fallida para tipo {TypeName}: {ErrorMessage}";
        public static string ServiceCollectionNotAvailable => ResourcesManager.GetLocalizedMessage("LogMessages_ServiceCollectionNotAvailable") ?? "Service collection no disponible para registrar reglas del plugin";
        public static string PluginRulesRegisteredSuccessfully => ResourcesManager.GetLocalizedMessage("LogMessages_PluginRulesRegisteredSuccessfully") ?? "Reglas de plugin registradas correctamente para el ensamblado: {AssemblyName}";
        public static string PluginRulesRegistrationFailed => ResourcesManager.GetLocalizedMessage("LogMessages_PluginRulesRegistrationFailed") ?? "Registro de reglas del plugin fallido para el ensamblado {AssemblyName}: {ErrorMessage}";
    }

    /// <summary>
    /// Configuration section names.
    /// </summary>
    public static string ConfigurationSections => ResourcesManager.GetLocalizedMessage("ConfigurationSections_Brms") ?? "Brms";


    /// <summary>
    /// Configuration paths and directories.
    /// </summary>
    public static class Paths
    {
        public static string BrmsNugetConfigEnv => ResourcesManager.GetLocalizedMessage("Paths_BrmsNugetConfigEnv") ?? "BRMS_NUGET_CONFIG";
        public static string NugetConfig => ResourcesManager.GetLocalizedMessage("Paths_NugetConfig") ?? "nuget.config";
        public static string NugetLocal => ResourcesManager.GetLocalizedMessage("Paths_NugetLocal") ?? "nuget-local";
        public static string LocalNugetPath => ResourcesManager.GetLocalizedMessage("Paths_LocalNugetPath") ?? "nuget-local";
        public static string NupkgPattern => ResourcesManager.GetLocalizedMessage("Paths_NupkgPattern") ?? "*.nupkg";
        public static string RulesDirectory => ResourcesManager.GetLocalizedMessage("Paths_RulesDirectory") ?? "Rules";
        public static string PluginsDirectory => ResourcesManager.GetLocalizedMessage("Paths_PluginsDirectory") ?? "Plugins";
        public static string LogsDirectory => ResourcesManager.GetLocalizedMessage("Paths_LogsDirectory") ?? "Logs";
    }
    public static string ConfigDirectory => ResourcesManager.GetLocalizedMessage("Paths_ConfigDirectory") ?? "Config";
    public static string TempDirectory => ResourcesManager.GetLocalizedMessage("Paths_TempDirectory") ?? "Temp";
    public static string CacheDirectory => ResourcesManager.GetLocalizedMessage("Paths_CacheDirectory") ?? "Cache";
}

/// <summary>
/// .NET Framework related constants.
/// </summary>
public static class NetFrameworks
{
    public static string NetCoreApp => ResourcesManager.GetLocalizedMessage("NetFrameworks_NetCoreApp") ?? "netcoreapp";
    public static string Net => ResourcesManager.GetLocalizedMessage("NetFrameworks_Net") ?? "net";
    public static string DllExtension => ResourcesManager.GetLocalizedMessage("NetFrameworks_DllExtension") ?? ".dll";
    public static string Net90 => ResourcesManager.GetLocalizedMessage("NetFrameworks_Net90") ?? "net9.0";
    public static string Net80 => ResourcesManager.GetLocalizedMessage("NetFrameworks_Net80") ?? "net8.0";
    public static string Net70 => ResourcesManager.GetLocalizedMessage("NetFrameworks_Net70") ?? "net7.0";
    public static string Net60 => ResourcesManager.GetLocalizedMessage("NetFrameworks_Net60") ?? "net6.0";
    public static string NetStandard21 => ResourcesManager.GetLocalizedMessage("NetFrameworks_NetStandard21") ?? "netstandard2.1";
    public static string NetStandard20 => ResourcesManager.GetLocalizedMessage("NetFrameworks_NetStandard20") ?? "netstandard2.0";
}

/// <summary>
/// JSON Schema format constants.
/// </summary>
public static class SchemaFormats
{
    public static string DateTimeFormat => ResourcesManager.GetLocalizedMessage("SchemaFormats_DateTimeFormat") ?? "date-time";
    public static string DateTimeDescription => ResourcesManager.GetLocalizedMessage("SchemaFormats_DateTimeDescription") ?? "Date and time in ISO 8601 format";
    public static string DateFormat => ResourcesManager.GetLocalizedMessage("SchemaFormats_DateFormat") ?? "date";
    public static string DateDescription => ResourcesManager.GetLocalizedMessage("SchemaFormats_DateDescription") ?? "Date in YYYY-MM-DD format";
    public static string EmailFormat => ResourcesManager.GetLocalizedMessage("SchemaFormats_EmailFormat") ?? "email";
    public static string PhoneFormat => ResourcesManager.GetLocalizedMessage("SchemaFormats_PhoneFormat") ?? "phone";
    public static string UriFormat => ResourcesManager.GetLocalizedMessage("SchemaFormats_UriFormat") ?? "uri";
    public static string ValidValuesTemplate => ResourcesManager.GetLocalizedMessage("SchemaFormats_ValidValuesTemplate") ?? "Valid values: {0}";
    public static string ValueSeparator => ResourcesManager.GetLocalizedMessage("SchemaFormats_ValueSeparator") ?? ", ";
    public static string DescriptionSeparator => ResourcesManager.GetLocalizedMessage("SchemaFormats_DescriptionSeparator") ?? ". ";
    public static string MinimumDateTemplate => ResourcesManager.GetLocalizedMessage("SchemaFormats_MinimumDateTemplate") ?? "Minimum date: {0}";
    public static string MaximumDateTemplate => ResourcesManager.GetLocalizedMessage("SchemaFormats_MaximumDateTemplate") ?? "Maximum date: {0}";
    public static string LengthTemplate => ResourcesManager.GetLocalizedMessage("SchemaFormats_LengthTemplate") ?? "Length: {0}";
    public static string MinimumLengthTemplate => ResourcesManager.GetLocalizedMessage("SchemaFormats_MinimumLengthTemplate") ?? "Minimum length: {0}";
    public static string MaximumLengthTemplate => ResourcesManager.GetLocalizedMessage("SchemaFormats_MaximumLengthTemplate") ?? "Maximum length: {0}";
    public static string MinimumItemsTemplate => ResourcesManager.GetLocalizedMessage("SchemaFormats_MinimumItemsTemplate") ?? "Minimum items: {0}";
    public static string MaximumItemsTemplate => ResourcesManager.GetLocalizedMessage("SchemaFormats_MaximumItemsTemplate") ?? "Maximum items: {0}";
}

/// <summary>
/// Error codes for different types of failures.
/// </summary>
public static class ErrorCodes
{
    public static int RuleNotFound => int.TryParse(ResourcesManager.GetLocalizedMessage("ErrorCodes_RuleNotFound"), out int value) ? value : 1001;
    public static int InvalidConfiguration => int.TryParse(ResourcesManager.GetLocalizedMessage("ErrorCodes_InvalidConfiguration"), out int value) ? value : 1002;
    public static int RuleExecutionFailed => int.TryParse(ResourcesManager.GetLocalizedMessage("ErrorCodes_RuleExecutionFailed"), out int value) ? value : 1003;
    public static int PluginLoadFailed => int.TryParse(ResourcesManager.GetLocalizedMessage("ErrorCodes_PluginLoadFailed"), out int value) ? value : 1004;
    public static int ValidationFailed => int.TryParse(ResourcesManager.GetLocalizedMessage("ErrorCodes_ValidationFailed"), out int value) ? value : 1005;
    public static int TimeoutExceeded => int.TryParse(ResourcesManager.GetLocalizedMessage("ErrorCodes_TimeoutExceeded"), out int value) ? value : 1006;
}

/// <summary>
/// Timeout values in milliseconds.
/// </summary>
public static class Timeouts
{
    public static int DefaultRuleTimeout => int.TryParse(ResourcesManager.GetLocalizedMessage("Timeouts_DefaultRuleTimeout"), out int value) ? value : 30000; // 30 seconds
    public static int DefaultAsyncTimeout => int.TryParse(ResourcesManager.GetLocalizedMessage("Timeouts_DefaultAsyncTimeout"), out int value) ? value : 60000; // 60 seconds
    public static int DefaultPluginLoadTimeout => int.TryParse(ResourcesManager.GetLocalizedMessage("Timeouts_DefaultPluginLoadTimeout"), out int value) ? value : 10000; // 10 seconds
    public static int DefaultCacheTimeout => int.TryParse(ResourcesManager.GetLocalizedMessage("Timeouts_DefaultCacheTimeout"), out int value) ? value : 300000; // 5 minutes
    public static int DefaultValidation => int.TryParse(ResourcesManager.GetLocalizedMessage("Timeouts_DefaultValidation"), out int value) ? value : 5000;
}

/// <summary>
/// JSON path constants.
/// </summary>
public static class JsonPath
{
    public static string Root => ResourcesManager.GetLocalizedMessage("JsonPath_Root") ?? "$";
    public static string Current => ResourcesManager.GetLocalizedMessage("JsonPath_Current") ?? "@";
    public static string Wildcard => ResourcesManager.GetLocalizedMessage("JsonPath_Wildcard") ?? "*";
    public static string RecursiveDescent => ResourcesManager.GetLocalizedMessage("JsonPath_RecursiveDescent") ?? "..";
    public static string ArraySlice => ResourcesManager.GetLocalizedMessage("JsonPath_ArraySlice") ?? ":";
    public static string FilterStart => ResourcesManager.GetLocalizedMessage("JsonPath_FilterStart") ?? "?(";
    public static string FilterEnd => ResourcesManager.GetLocalizedMessage("JsonPath_FilterEnd") ?? ")";
}


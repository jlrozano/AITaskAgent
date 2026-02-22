# BRMS.StdRules

## Información General

| Propiedad | Valor |
|-----------|-------|
| **Nombre del Módulo** | BRMS.StdRules |
| **Namespace** | BRMS.StdRules |
| **Versión** | 1.0.4 |
| **Dependencias** | Microsoft.ClearScript (2.7.4), Microsoft.ClearScript.V8 (7.4.5), Microsoft.Extensions.Http (9.0.0), Newtonsoft.Json (13.0.3), Dapper (2.1.35), BRMS.Abstractions (referencia de proyecto) |
| **Propósito** | Paquete de reglas estándar para el motor BRMS que proporciona validadores, normalizadores y transformadores predefinidos para casos de uso comunes en sistemas de gestión de reglas de negocio |

## Entidades Principales

### 1. StdRulesPlugin

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | - |
| **Implementa** | IBRMSPlugin |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Plugin principal que registra todas las reglas estándar (validadores y normalizadores) en el sistema BRMS |

**Observaciones:**
- Actúa como punto de entrada para el registro de todas las reglas estándar
- Maneja el registro y desregistro de reglas dinámicas de JavaScript
- Proporciona soporte para configuración dinámica de reglas
- Implementa el patrón de plugin para integración con el motor BRMS

**Ejemplo de uso:**
```csharp
// El plugin se registra automáticamente cuando se carga el paquete
// No requiere configuración manual adicional
```

### 2. AdvancedCleanNormalizer

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Normalizer |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Normalizador que realiza limpieza avanzada de cadenas con múltiples reglas de transformación |

**Observaciones:**
- Utiliza expresiones regulares precompiladas para mejor rendimiento
- Maneja caracteres especiales como '#' con reglas específicas
- Convierte patrones de caracteres repetidos a null
- Normaliza espacios, puntos y caracteres especiales
- Soporta solo tipos de entrada String

**Ejemplo de uso:**
```csharp
// Configuración en schema JSON
{
  "name": "AdvancedClean",
  "propertyPath": "descripcion"
}
```

### 3. DatabaseNormalizer

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Normalizer |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules.DataBase |
| **Propósito** | Normalizador que utiliza consultas SQL para normalizar valores de campos mediante base de datos |

**Observaciones:**
- Soporta parámetros dinámicos: @Value, {oldValue.Propiedad}, {newValue.Propiedad}
- Requiere configuración de base de datos mediante IDataBaseQuery
- Marcado como Scoped para inyección de dependencias
- Soporta cualquier tipo de entrada (RuleInputType.Any)
- Maneja valores null de forma segura

**Ejemplo de uso:**
```csharp
// Configuración en schema JSON
{
  "name": "DatabaseNormalizer",
  "propertyPath": "codigo",
  "sqlSmtp": "SELECT UPPER(@Value)",
  "dataBaseName": "MainDB"
}
```

### 4. JsScriptNormalizer

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | JsScriptRule |
| **Implementa** | INormalizer |
| **Namespace** | BRMS.StdRules.JsScript |
| **Propósito** | Normalizador que ejecuta scripts JavaScript para transformar datos JSON de forma dinámica |

**Observaciones:**
- Permite lógica de normalización personalizada mediante JavaScript
- Soporta notificación de cambios configurable (MustNotifyChange)
- Proporciona acceso a console para debugging
- Maneja errores de ejecución de script de forma segura
- Retorna ScriptNormalizerResult con información detallada

**Ejemplo de uso:**
```csharp
// Configuración en schema JSON
{
  "name": "JSNormalizer",
  "propertyPath": "campo",
  "script": "return value.toUpperCase();",
  "mustNotifyChange": true
}
```

### 5. HpptNormalizer

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Normalizer |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules.Hppt |
| **Propósito** | Normalizador que realiza llamadas HTTP para obtener valores normalizados desde servicios externos |

**Observaciones:**
- Soporta métodos HTTP GET, POST, PUT, DELETE
- Permite configuración de headers personalizados
- Maneja timeouts y reintentos
- Soporta autenticación básica y bearer token
- Procesa respuestas JSON para extraer valores específicos

### 6. CountValidator

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Validator |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Validador que verifica el número de elementos en colecciones o la longitud de cadenas |

**Observaciones:**
- Soporta validación de arrays, listas y cadenas
- Permite configurar valores mínimos y máximos
- Maneja valores null de forma segura
- Proporciona mensajes de error descriptivos
- Soporta tipos Array y String

### 7. DatabaseValidator

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Validator |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules.DataBase |
| **Propósito** | Validador que ejecuta consultas SQL para verificar la validez de valores contra base de datos |

**Observaciones:**
- Utiliza consultas SQL personalizadas para validación
- Soporta parámetros dinámicos como DatabaseNormalizer
- Requiere inyección de dependencia IDataBaseQuery
- Marcado como Scoped para gestión de ciclo de vida
- Retorna true/false basado en resultado de consulta

### 8. RegexValidator

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Validator |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Validador que verifica si un valor cumple con un patrón de expresión regular |

**Observaciones:**
- Utiliza expresiones regulares compiladas para mejor rendimiento
- Soporta configuración de opciones de regex (IgnoreCase, Multiline, etc.)
- Maneja valores null retornando true por defecto
- Proporciona mensajes de error personalizables
- Optimizado para validaciones de formato comunes

### 9. JsScriptValidator

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | JsScriptRule |
| **Implementa** | IValidator |
| **Namespace** | BRMS.StdRules.JsScript |
| **Propósito** | Validador que ejecuta scripts JavaScript para implementar lógica de validación personalizada |

**Observaciones:**
- Permite validaciones complejas mediante JavaScript
- Soporta acceso completo al contexto de ejecución
- Proporciona console para debugging de scripts
- Maneja errores de ejecución de forma segura
- Retorna ScriptValidatorResult con información detallada

### 10. RangeNumberValidator

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Validator |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Validador que verifica si un valor numérico está dentro de un rango específico |

**Observaciones:**
- Soporta validación de enteros y números decimales
- Permite configurar valores mínimos y máximos opcionales
- Maneja conversiones de tipo de forma segura
- Soporta rangos abiertos (solo mínimo o solo máximo)
- Proporciona mensajes de error específicos para cada tipo de violación

### 11. RequiredFieldValidator

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Validator |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Validador que verifica que un campo tenga un valor no nulo y no vacío |

**Observaciones:**
- Valida campos obligatorios en objetos JSON
- Considera null, cadenas vacías y espacios en blanco como inválidos
- Soporta todos los tipos de entrada
- Proporciona mensajes de error claros
- Esencial para validaciones de integridad de datos

### 12. TextLengthValidator

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Validator |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Validador que verifica la longitud de cadenas de texto |

**Observaciones:**
- Permite configurar longitud mínima y máxima
- Maneja valores null retornando true por defecto

### 13. CollapseSpacesNormalizer

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Normalizer |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Colapsa múltiples espacios internos a uno solo y recorta espacios al inicio y fin |

**Ejemplo de uso:**
```json
{
  "name": "CollapseSpaces",
  "propertyPath": "persona.direccion.calle"
}
```

### 14. ToTitleCaseNormalizer

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Normalizer |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Convierte cadenas a Title Case respetando reglas de cultura |

**Observaciones:**
- Propiedad opcional `culture` (por defecto "es-ES")

**Ejemplo de uso:**
```json
{
  "name": "ToTitleCase",
  "propertyPath": "persona.nombre",
  "culture": "es-ES"
}
```

### 15. RemoveDiacriticsNormalizer

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Normalizer |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Elimina diacríticos (acentos, tildes) de cadenas |

**Observaciones:**
- Propiedad opcional `preserveEnie` (true por defecto) para preservar ñ/Ñ

**Ejemplo de uso:**
```json
{
  "name": "RemoveDiacritics",
  "propertyPath": "persona.apellido1",
  "preserveEnie": true
}
```

### 16. E164PhoneNormalizer

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Normalizer |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Convierte teléfonos al formato internacional E.164 usando libphonenumber |

**Observaciones:**
- Propiedad `regionIso` (por defecto "ES") para indicar la región usada al parsear/validar
- Si el número no es válido para la región indicada, no realiza cambios

**Ejemplo de uso:**
```json
{
  "name": "PhoneToE164",
  "propertyPath": "persona.telefono",
  "regionIso": "ES"
}
```
- Soporta solo validación de cadenas
- Proporciona mensajes de error específicos
- Útil para validaciones de formato de campos de texto

### 13. DefaultValueNormalizer

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Normalizer |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Normalizador que asigna un valor por defecto cuando el campo es null o vacío |

**Observaciones:**
- Permite configurar valor por defecto mediante propiedad DefaultValue
- Soporta solo tipos String
- No modifica valores existentes no vacíos
- Útil para garantizar valores mínimos en campos opcionales
- Maneja conversiones de tipo de forma segura

### 14. TrimSpacesNormalizer

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Normalizer |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Normalizador que elimina espacios en blanco al inicio y final de cadenas |

**Observaciones:**
- Aplica Trim() a valores de cadena
- Maneja valores null de forma segura
- Soporta solo tipos String
- Operación simple pero esencial para limpieza de datos
- No modifica espacios internos de la cadena

### 15. ToUpperNormalizer

| Propiedad | Valor |
|-----------|-------|
| **Tipo** | Clase |
| **Hereda de** | Normalizer |
| **Implementa** | - |
| **Namespace** | BRMS.StdRules |
| **Propósito** | Normalizador que convierte cadenas de texto a mayúsculas |

**Observaciones:**
- Aplica ToUpper() a valores de cadena
- Maneja valores null retornando sin cambios
- Soporta solo tipos String
- Útil para normalización de códigos y identificadores
- Respeta la configuración de cultura del sistema

## Características Técnicas

### Arquitectura de Plugins
- **Registro Automático**: Las reglas se registran automáticamente al cargar el plugin
- **Configuración Dinámica**: Soporte para reglas JavaScript dinámicas
- **Inyección de Dependencias**: Integración completa con el contenedor DI de .NET
- **Ciclo de Vida**: Gestión automática del ciclo de vida de reglas Scoped

### Soporte para JavaScript
- **Motor V8**: Utiliza Microsoft.ClearScript.V8 para ejecución de JavaScript
- **Contexto Completo**: Acceso a oldValue, newValue y contexto de ejecución
- **Console Debugging**: Soporte para console.log en scripts
- **Manejo de Errores**: Captura y reporte de errores de ejecución

### Integración con Base de Datos
- **Múltiples Proveedores**: Soporte para diferentes bases de datos via Dapper
- **Parámetros Dinámicos**: Sistema avanzado de reemplazo de parámetros
- **Conexiones Configurables**: Gestión de múltiples conexiones de base de datos
- **Transacciones**: Soporte para operaciones transaccionales

### Validación y Normalización
- **Tipos Soportados**: String, Number, Array, Object, Any
- **Expresiones Regulares**: Patrones precompilados para mejor rendimiento
- **Mensajes Personalizables**: Sistema de mensajes de error configurable
- **Logging Estructurado**: Logging detallado con contexto de ejecución

## Inicialización

### Registro del Plugin
```csharp
// El plugin se registra automáticamente cuando se carga el paquete NuGet
// No requiere configuración manual adicional en la mayoría de casos
```

### Configuración de Dependencias
```csharp
// Para reglas que requieren base de datos
services.AddScoped<IDataBaseQuery, DatabaseQueryImplementation>();

// Para reglas HTTP
services.AddHttpClient();
```

### Ejemplo de Schema con Reglas Estándar
```json
{
  "name": "PersonSchema",
  "dataSchema": {
    "type": "object",
    "properties": {
      "nombre": { "type": "string" },
      "email": { "type": "string" },
      "edad": { "type": "integer" }
    }
  },
  "rules": {
    "normalizers": [
      {
        "name": "TrimSpaces",
        "propertyPath": "nombre"
      },
      {
        "name": "ToUpper",
        "propertyPath": "nombre"
      },
      {
        "name": "DefaultValue",
        "propertyPath": "email",
        "defaultValue": "sin-email@ejemplo.com"
      }
    ],
    "validators": [
      {
        "name": "RequiredField",
        "propertyPath": "nombre"
      },
      {
        "name": "TextLength",
        "propertyPath": "nombre",
        "minLength": 2,
        "maxLength": 50
      },
      {
        "name": "Regex",
        "propertyPath": "email",
        "pattern": "^[\\w-\\.]+@([\\w-]+\\.)+[\\w-]{2,4}$"
      },
      {
        "name": "RangeNumber",
        "propertyPath": "edad",
        "minValue": 0,
        "maxValue": 120
      }
    ]
  }
}
```

## Extensibilidad

### Creación de Reglas Personalizadas
El módulo BRMS.StdRules sirve como ejemplo para crear reglas personalizadas:

```csharp
[RuleName("MiNormalizadorPersonalizado")]
[Description("Descripción de mi normalizador")]
[SupportedTypes(RuleInputType.String)]
public class MiNormalizadorPersonalizado : Normalizer
{
    protected override Task<NormalizerResult> Execute(
        BRMSExecutionContext context, 
        CancellationToken cancellationToken)
    {
        // Implementación personalizada
        var value = context.NewValue.GetValueAs<string>(PropertyPath);
        
        // Lógica de normalización
        var normalizedValue = ProcessValue(value);
        
        context.NewValue.SetValueWithType(PropertyPath, normalizedValue);
        
        return Task.FromResult(new NormalizerResult(this, context));
    }
    
    private string ProcessValue(string value)
    {
        // Lógica específica
        return value?.Trim().ToLower();
    }
}
```

### Reglas JavaScript Dinámicas
```javascript
// Ejemplo de normalizador JavaScript
function normalize(value, oldValue, newValue, context) {
    if (!value) return null;
    
    // Lógica personalizada en JavaScript
    return value.toString().replace(/[^a-zA-Z0-9]/g, '');
}

// Ejemplo de validador JavaScript
function validate(value, oldValue, newValue, context) {
    if (!value) return false;
    
    // Validación personalizada
    return value.length >= 5 && value.length <= 20;
}
```

## Verificación

Este documento ha sido generado basándose en el análisis del código fuente del módulo BRMS.StdRules versión 1.0.4. La información presentada refleja la implementación actual y las capacidades reales del sistema.

**Archivos analizados:**
- BRMS.StdRules.csproj
- StdRulesPlugin.cs
- AdvancedCleanNormalizer.cs
- DatabaseNormalizer.cs
- JsScriptNormalizer.cs
- HpptNormalizer.cs
- Múltiples validadores y normalizadores estándar
- Archivos de configuración y recursos

**Fecha de análisis:** Diciembre 2024
**Versión del código:** 1.0.4
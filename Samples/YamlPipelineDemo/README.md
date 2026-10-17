# YAML Pipeline Demo - Document Processing Pipeline with LLM Reasoning

Un pipeline de 3 pasos que demuestra el engine YAML con integración OpenRouter y **observación de razonamiento LLM en tiempo real**. El pipeline clasifica documentos, extrae información según el tipo, y genera un resumen con recomendaciones.

## Características

- ✨ **Observación de Razonamiento LLM**: Captura y muestra el pensamiento interno del LLM en tiempo real
- 🔄 **Pipeline de 3 Pasos**: Clasificación → Extracción → Resumen
- 📊 **Schemas Compilados**: JSON Schemas compilados a tipos C# con Roslyn
- 🎯 **Eventos en Tiempo Real**: Sistema de observabilidad con `IEventChannel`

## Estructura

- **Step 1 - Classify**: Detecta el tipo de documento (INVOICE, REPORT, CONTRACT, OTHER)
- **Step 2 - Extract**: Extrae campos clave según el tipo detectado
- **Step 3 - Summarize**: Genera un resumen ejecutivo con insights y recomendaciones

## Requisitos

- .NET 10 SDK (`net10.0`)
- Clave de API de OpenRouter

## Setup

### 1. Configurar OpenRouter API Key

El `appsettings.json` ya está preconfigurado con OpenRouter (proveedor `OpenRouter`, `ServiceType: openai`) y el rol `default` con el modelo `openai/gpt-oss-120b:free`.

Solo necesitas tu clave de API de OpenRouter (se expande desde `${OPENROUTER_API_KEY}`):

```bash
# Windows
set OPENROUTER_API_KEY=sk-or-v1-...

# Linux/macOS
export OPENROUTER_API_KEY=sk-or-v1-...
```

Para usar otro modelo, cambia `Roles:default:Model` (o sobrescríbelo en `appsettings.development.json`).

### 2. (Alternativa) Usar OpenAI Directamente

Si prefieres usar OpenAI en lugar de OpenRouter, crea `appsettings.development.json` (está en `.gitignore`); se carga siempre que exista, después de `appsettings.json`:

```json
{
  "AITaskAgent": {
    "LlmProviders": {
      "Providers": {
        "OpenAI": {
          "ServiceType": "openai",
          "BaseUrl": "https://api.openai.com/v1",
          "ApiKey": "${OPENAI_API_KEY}",
          "Inference": { "JsonSupport": true }
        }
      },
      "Roles": {
        "default": {
          "Provider": "OpenAI",
          "Model": "gpt-4o"
        }
      },
      "DefaultRole": "default"
    }
  }
}
```

Y configura:
```bash
export OPENAI_API_KEY=sk-...
```

### 3. Ejecutar

```bash
dotnet run
```

`Program.cs` carga `appsettings.development.json` (opcional) sin mirar `DOTNET_ENVIRONMENT`, y después las variables de entorno.

## Output Esperado

Durante la ejecución verás en amarillo el **contenido de cada `LlmResponseEvent`** (incluido el razonamiento, si el modelo lo emite) y el resto de eventos serializados como JSON. El filtro por `IsThinking` y la cabecera `[💭 LLM Thinking - ...]` están comentados en `Program.cs`, igual que la impresión de los resultados de los pasos 1 y 2; la salida siguiente es ilustrativa:

```
=== YAML Pipeline Engine Demo with LLM Reasoning ===

1. Compilando schemas JSON con Roslyn...
   Schemas compilados: DocumentInput, ClassificationOutput, ExtractionOutput, FinalOutput

2. Construyendo pipeline desde YAML...
   Pipeline 'DocumentProcessingPipeline' construido con 3 step(s)

3. Input creado: DocumentInput
   Implementa IStepResult: True

4. Ejecutando pipeline (observando razonamiento en tiempo real)...

[💭 LLM Thinking - step_1_classify]
Let me analyze this document content to determine its type.
This appears to be an invoice because:
1. It mentions "Invoice from [company]"
2. It specifies an amount in dollars ($1500.00)
3. It has a date (March 1, 2026)
4. It describes services rendered

The document type should be INVOICE with high confidence.

[💭 LLM Thinking - step_2_extract]
Now I need to extract key fields for an INVOICE:
- Vendor/Company: Acme Corporation
- Amount: $1500.00
- Date: March 1, 2026
- Description: Consulting services

...


=== Pipeline Results ===
HasError: False

[Step 1 - Classification]
Type: INVOICE, Confidence: 0.95

[Step 2 - Extraction]
Key Fields: {"vendor":"Acme Corporation","amount":"$1500.00","date":"March 1, 2026",...}

[Step 3 - Final Output]
Document Type: INVOICE
Summary: Invoice from Acme Corporation for $1500.00 of consulting services in March 2026...
Recommendation: Process payment and file in Q1 2026 expenses
```

## Archivos Clave

- `pipelines/main.yaml` - Definición del pipeline con 3 pasos
- `schemas/*.json` - JSON Schemas para inputs y outputs
- `prompts/*.md` - System prompts para cada paso
- `appsettings.json` - Configuración de OpenRouter y del rol `default`
- `appsettings.development.json` - Sobrescritura local opcional (excluida de git, no incluida en el repo)

## Cómo Funciona la Observación de Razonamiento

El programa usa el sistema de eventos `IEventChannel` para capturar `LlmResponseEvent` en tiempo real:

1. **Suscripción a Eventos**: Se crea una suscripción asincrónica al channel de eventos
2. **Filtrado**: el filtro por `IsThinking == true` está comentado; se muestra el `Content` de todo `LlmResponseEvent` y el resto de eventos como JSON
3. **Visualización**: Todo se muestra en **amarillo**
4. **Execution**: El razonamiento se captura **en paralelo** durante la ejecución del pipeline

Código relevante en [Program.cs](Program.cs#L66-L88):
```csharp
var eventSubscription = eventChannel?.CreateSubscription(capacity: 500);
if (eventSubscription != null)
{
    reasoningTask = Task.Run(async () =>
    {
        await foreach (var evt in eventSubscription.Reader.ReadAllAsync())
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            if (evt is LlmResponseEvent res)
            {
                Console.Write(res.Content);
            }
            else
            {
                Console.WriteLine(JsonConvert.SerializeObject(evt, Formatting.Indented));
            }
            Console.ResetColor();
        }
    });
}
```

## Notas Técnicas

- Los prompts se renderizan con `PromptTemplateResolver`, cuyo modelo es `{ Input, Context }`: `{{input.<campo>}}` accede a la entrada del paso (la salida del paso anterior) y `{{context.<clave>}}` a los metadatos del pipeline. No hay acceso por nombre a los resultados de otros pasos: cada paso recibe la salida del anterior como entrada.
- `pipelines/main.yaml` usa `{{inputData.<campo>}}`, que no corresponde a ese modelo, y `systemPrompt: "@classifier"`, que `FileTemplateProvider` busca como `@classifier.md`. Leyendo el código, ninguno de los dos se resolvería (sin comprobar en ejecución).
- Los schemas se compilan a tipos C# en tiempo de ejecución con Roslyn
- Cada paso se ejecuta secuencialmente con validación de dependencias (DAG)
- El sistema de eventos es **asincrónico** y no bloquea la ejecución del pipeline

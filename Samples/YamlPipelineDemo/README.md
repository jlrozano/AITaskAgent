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

- .NET 8+
- Clave de API de OpenRouter (para modelos con razonamiento como DeepSeek-R1)

## Setup

### 1. Configurar OpenRouter API Key (Recomendado para Razonamiento)

El `appsettings.development.json` ya está preconfigurado con OpenRouter y DeepSeek-R1 (modelo con razonamiento).

Solo necesitas tu clave de API de OpenRouter:

```bash
# Windows
set OPENROUTER_API_KEY=sk-or-v1-...

# Linux/macOS
export OPENROUTER_API_KEY=sk-or-v1-...
```

**Modelos disponibles con razonamiento** en `appsettings.development.json`:
- `deepseek/deepseek-r1:free` - DeepSeek R1 (gratis en OpenRouter)
- `openai/o1` - OpenAI O1 (requiere créditos)
- `openai/o1-mini` - OpenAI O1 Mini

### 2. (Alternativa) Usar OpenAI Directamente

Si prefieres usar OpenAI en lugar de OpenRouter, crea/modifica `appsettings.development.json`:

```json
{
  "AITaskAgent": {
    "LlmProviders": {
      "Providers": {
        "default": {
          "Provider": "OpenAI",
          "BaseUrl": "https://api.openai.com/v1",
          "ApiKey": "${OPENAI_API_KEY}",
          "Model": "gpt-4o",
          "JsonCapability": "JsonObject",
          "Reasoning": true
        }
      },
      "DefaultProvider": "default"
    }
  }
}
```

Y configura:
```bash
export OPENAI_API_KEY=sk-...
```

### 3. Ejecutar en Development Mode

```bash
# Windows
set DOTNET_ENVIRONMENT=Development
dotnet run

# Linux/macOS
export DOTNET_ENVIRONMENT=Development
dotnet run
```

## Output Esperado

Durante la ejecución verás el **razonamiento interno del LLM** en amarillo:

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
- `appsettings.development.json` - Configuración de OpenAI (local, excluido de git)

## Cómo Funciona la Observación de Razonamiento

El programa usa el sistema de eventos `IEventChannel` para capturar `LlmResponseEvent` en tiempo real:

1. **Suscripción a Eventos**: Se crea una suscripción asincrónica al channel de eventos
2. **Filtrado**: Solo se muestran eventos donde `IsThinking == true` (razonamiento interno)
3. **Visualización**: El pensamiento se muestra en **amarillo** con el identificador del step
4. **Execution**: El razonamiento se captura **en paralelo** durante la ejecución del pipeline

Código relevante en [Program.cs](Program.cs#L55-L76):
```csharp
var eventSubscription = eventChannel?.CreateSubscription(capacity: 500);
if (eventSubscription != null)
{
    reasoningTask = Task.Run(async () =>
    {
        await foreach (var evt in eventSubscription.Reader.ReadAllAsync())
        {
            if (evt is LlmResponseEvent llmEvent && llmEvent.IsThinking)
            {
                // Mostrar pensamiento en amarillo
                Console.WriteLine(llmEvent.Content);
            }
        }
    });
}
```

## Notas Técnicas

- El pipeline usa **variables de contexto** para pasar datos entre pasos:
  - `{{input.content}}` - accede al contenido del documento
  - `{{step_1_classify.type}}` - accede a outputs del paso anterior
- Los schemas se compilan a tipos C# en tiempo de ejecución con Roslyn
- Cada paso se ejecuta secuencialmente con validación de dependencias (DAG)
- El sistema de eventos es **asincrónico** y no bloquea la ejecución del pipeline
- La propiedad `Reasoning: true` en appsettings permite al LLM usar más tokens para razonamiento

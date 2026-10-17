# Configuración de Precios de LLM

## Descripción

Los precios de cada modelo se configuran en `appsettings.json`, dentro de la lista `Models` de cada proveedor (`AITaskAgent:LlmProviders:Providers:<proveedor>:Models`). Con ellos `LlmProvider.CalculateCost` calcula el coste real de cada llamada.

## Propiedades de Configuración

Cada entrada de `Models` (`LlmModelSettings`) admite:

- **`Name`**: nombre del modelo; se compara sin distinguir mayúsculas con el modelo de la llamada
- **`InputPricePerMillion`**: Precio en USD por millón de tokens de entrada
- **`OutputPricePerMillion`**: Precio en USD por millón de tokens de salida
- **`CachedInputPricePerMillion`** (opcional): precio de los tokens de entrada servidos desde la caché de prompt (ver 2.1)

## Ejemplo de Configuración

```json
{
  "AITaskAgent": {
    "LlmProviders": {
      "Providers": {
        "Google": {
          "ServiceType": "Gemini",
          "BaseUrl": "https://generativelanguage.googleapis.com/v1beta/openai/",
          "ApiKey": "YOUR_API_KEY",
          "Models": [
            { "Name": "gemini-2.5-flash", "InputPricePerMillion": 0.075, "OutputPricePerMillion": 0.30 }
          ]
        },
        "NVidia": {
          "ServiceType": "OpenAI",
          "BaseUrl": "https://integrate.api.nvidia.com/v1",
          "ApiKey": "YOUR_API_KEY",
          "Models": [
            { "Name": "meta/llama-3.1-8b-instruct", "InputPricePerMillion": 0.20, "OutputPricePerMillion": 0.20 }
          ]
        }
      },
      "Roles": {
        "default": {
          "Provider": "Google",
          "Model": "gemini-2.5-flash",
          "Inference": { "Temperature": 0.5, "MaxTokens": 16000, "JsonSupport": true }
        },
        "Writers": {
          "Provider": "NVidia",
          "Model": "meta/llama-3.1-8b-instruct",
          "Inference": { "Temperature": 0.5, "MaxTokens": 16000, "JsonSupport": false }
        }
      },
      "DefaultRole": "default"
    }
  }
}
```

`ServiceType` elige el `ILlmService` registrado por nombre (si se omite, se usa la clave del proveedor); los valores de este ejemplo son ilustrativos y deben coincidir con el `Name` de los servicios que registres.

## Cómo Funciona

### 1. Información de Tokens

El LLM proporciona automáticamente la siguiente información en cada respuesta:

- **`PromptTokens`**: Número de tokens de entrada (tu prompt)
- **`CompletionTokens`**: Número de tokens de salida (respuesta del LLM)
- **`TokensUsed`**: Total de tokens (entrada + salida)

### 2. Cálculo de Coste

Cuando el servicio no informa coste propio, `BaseLlmStep` llama a `LlmProvider.CalculateCost` buscando primero el modelo que devolvió la respuesta y, si no tiene precio, el modelo pedido (Azure responde con el nombre versionado del modelo). La fórmula, sin tokens cacheados, es:

```csharp
var promptCost = promptTokens / 1_000_000m * InputPricePerMillion;
var completionCost = completionTokens / 1_000_000m * OutputPricePerMillion;
var totalCost = promptCost + completionCost;
```

### 2.1 Tokens de entrada cacheados

Cada modelo de `Providers:<p>:Models` admite además el campo opcional **`CachedInputPricePerMillion`**: el precio
por millón de los tokens de entrada que el proveedor sirve desde su caché de prompt (OpenAI/Azure los informan en
`usage.prompt_tokens_details.cached_tokens` y llegan como `LlmResult.CachedPromptTokens`). Esos tokens van incluidos en
`PromptTokens`: `LlmProvider.CalculateCost` cobra los cacheados a `CachedInputPricePerMillion` y el resto a
`InputPricePerMillion`. Si el campo no está, los cacheados se cobran a precio de entrada normal (el coste sale por
exceso, nunca por defecto).

```json
{ "Name": "gpt-5.4", "InputPricePerMillion": 2.5, "OutputPricePerMillion": 15, "CachedInputPricePerMillion": 0.25 }
```

### 3. Modelos sin precio

No hay precios predefinidos en el código. Si el modelo no está en `Models`, o le falta `InputPricePerMillion` u `OutputPricePerMillion`, el coste es **`null`** (desconocido), no $0.00: un coste desconocido no debe leerse como una llamada gratuita.

## Precios de Referencia (Enero 2026)

Orientativos: el código no los usa y no se han contrastado con las tarifas actuales de cada proveedor.

### Google Gemini
- **gemini-2.5-flash**: $0.075 / $0.30 por millón de tokens
- **gemini-2.0-flash**: $0.10 / $0.40 por millón de tokens
- **gemini-1.5-pro**: $1.25 / $5.00 por millón de tokens

### OpenAI
- **gpt-4o**: $5.00 / $15.00 por millón de tokens
- **gpt-4o-mini**: $0.15 / $0.60 por millón de tokens
- **gpt-4-turbo**: $10.00 / $30.00 por millón de tokens

### Anthropic Claude
- **claude-3.5-sonnet**: $3.00 / $15.00 por millón de tokens
- **claude-3-haiku**: $0.25 / $1.25 por millón de tokens

### Meta Llama (via NVIDIA)
- **llama-3.1-8b-instruct**: $0.20 / $0.20 por millón de tokens
- **llama-3.1-70b-instruct**: $0.60 / $0.60 por millón de tokens

### DeepSeek
- **deepseek-chat**: $0.14 / $0.28 por millón de tokens
- **deepseek-coder**: $0.14 / $0.28 por millón de tokens

## Acceso al Coste en el Código

El coste calculado está disponible en:

```csharp
// En LlmResult (lo rellena BaseLlmStep tras la llamada; un ILlmService invocado directamente no lo calcula)
LlmResult result = await llmService.InvokeAsync(request);
decimal? cost = result.CostUsd; // Coste en USD

// En el resultado de un paso LLM (ILlmStepResult / LlmStepResult)
IStepResult result = await step.ExecuteAsync(input, context, 1, null, CancellationToken.None);
if (result is ILlmStepResult llmResult)
{
    decimal? cost = llmResult.CostUsd;
    Console.WriteLine($"Coste de la llamada: ${cost:F6}");
}
```

## Logging

El coste viaja en `LlmResponseEvent.CostUsd` y `BaseLlmStep` lo guarda en `context.Metadata["CostUsd"]` y lo suma a la métrica `Metrics.LlmCost`.

## Notas Importantes

1. **Los precios son opcionales**: Si no los configuras, `CostUsd` queda a `null`
2. **Actualiza los precios regularmente**: Los proveedores de LLM cambian sus precios frecuentemente
3. **Verifica con tu proveedor**: Los precios pueden variar según tu plan o región
4. **Precios en USD**: Todos los precios se manejan en dólares estadounidenses

using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Reports;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace CodebaseAnalysis.Core.Documents;

/// <summary>
/// Describe una imagen con un modelo multimodal sin tocar el framework (plan v2 §4.4, §7.4): una conversación nueva con
/// un único mensaje <c>user</c> cuyo <see cref="Message.Native"/> es el <c>JObject</c> de OpenAI con el texto y la
/// imagen en <c>data:</c> y <c>detail: high</c>. El conversor de OpenAI (y el de Azure, que lo hereda) lo reenvía tal cual.
/// </summary>
public sealed class ImageDescriber(LlmRole role, ITemplateProvider templates, ILogger? logger = null)
{
    public const string TemplateName = "document_image_describe";

    /// <summary>Tope de la imagen enviada.</summary>
    public const long MaxImageBytes = 8L * 1024 * 1024;

    public string Model => role.Model;

    /// <summary>
    /// Describe la imagen o, con <paramref name="question"/>, responde a esa pregunta sobre ella.
    /// </summary>
    /// <exception cref="KnowledgeOperationException"><c>image_description_unavailable</c> (demasiado grande o rechazada por el proveedor) o <c>llm_error</c>.</exception>
    public async Task<string> DescribeAsync(byte[] image, string mediaType, string? question, CancellationToken cancellationToken)
    {
        if (image.LongLength > MaxImageBytes)
        {
            throw new KnowledgeOperationException(KnowledgeErrorCodes.ImageDescriptionUnavailable,
                $"La imagen ocupa {image.LongLength / (1024 * 1024)} MB; el máximo para describirla es {MaxImageBytes / (1024 * 1024)} MB.");
        }

        var prompt = BuildPrompt(question);
        var conversation = new ConversationContext(maxTokens: 1_000_000);
        conversation.AddMessage(BuildMessage(prompt, image, mediaType));

        var request = new LlmRequest
        {
            Conversation = conversation,
            Model = role.Model,
            Temperature = role.Inference?.Temperature,
            MaxTokens = role.Inference?.MaxTokens ?? 16384,
            UseMaxCompletionTokens = role.Inference?.UseMaxCompletionTokens ?? role.Provider.Inference?.UseMaxCompletionTokens ?? false,
            UseStreaming = false,
            BaseUrl = role.Provider.BaseUrl,
            ApiKey = role.Provider.ApiKey,
            ApiVersion = role.Provider.ApiVersion,
            UseSlidingWindow = false
        };

        LlmResult result;
        try
        {
            result = await role.Provider.Service.InvokeAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new KnowledgeOperationException(KnowledgeErrorCodes.LlmError, $"El modelo {role.Model} falló al describir la imagen: {ex.Message}");
        }

        if (!result.IsSuccess)
        {
            var message = result.Error?.Message ?? "error desconocido";
            // Un 400 del proveedor es que no acepta la imagen (formato, tamaño, modelo sin visión); el resto, un fallo del LLM.
            var rejected = message.Contains("400", StringComparison.Ordinal) || message.Contains("image", StringComparison.OrdinalIgnoreCase);
            throw new KnowledgeOperationException(
                rejected ? KnowledgeErrorCodes.ImageDescriptionUnavailable : KnowledgeErrorCodes.LlmError,
                $"El proveedor no pudo describir la imagen: {message}");
        }

        logger?.LogInformation("Imagen descrita con {Model}: {Tokens} tokens (fuera del recuento del turno)", result.Model ?? role.Model, result.TokensUsed);
        return result.Content.Trim().Length == 0
            ? throw new KnowledgeOperationException(KnowledgeErrorCodes.LlmError, "El modelo devolvió una descripción vacía.")
            : result.Content.Trim();
    }

    /// <summary>La plantilla genérica y, si hay pregunta, la pregunta detrás (no se interpola: es texto del usuario).</summary>
    internal string BuildPrompt(string? question)
    {
        var prompt = templates.Render(TemplateName, new Dictionary<string, object?>())
            ?? throw new InvalidOperationException($"La plantilla '{TemplateName}' no produjo contenido.");
        return string.IsNullOrWhiteSpace(question)
            ? prompt.Trim()
            : prompt.Trim() + Environment.NewLine + Environment.NewLine
              + "En lugar de la descripción completa, responde solo a esta pregunta sobre la imagen, con la misma honestidad sobre lo que no se distingue:"
              + Environment.NewLine + question.Trim();
    }

    /// <summary>El mensaje multimodal: texto e imagen en <c>data:</c> con <c>detail: high</c>.</summary>
    public static Message BuildMessage(string prompt, byte[] image, string mediaType) => new()
    {
        Role = "user",
        Content = prompt,
        Native = new NativeObject
        {
            Provider = "OpenAI",
            TypeName = "JObject",
            Value = new JObject
            {
                ["role"] = "user",
                ["content"] = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = prompt },
                    new JObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JObject
                        {
                            ["url"] = $"data:{mediaType};base64,{Convert.ToBase64String(image)}",
                            ["detail"] = "high"
                        }
                    }
                }
            }
        }
    };
}

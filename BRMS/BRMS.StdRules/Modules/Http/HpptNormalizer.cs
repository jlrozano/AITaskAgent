using System.ComponentModel;
using System.Text;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using BRMS.StdRules.Http.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BRMS.StdRules.Modules.Http;

[RuleName("HpptNormalizer")]
[Description(ResourcesKeys.Desc_HpptNormalizer_Description)]
[SupportedTypes(RuleInputType.Any)]
public class HpptNormalizer(IHttpClientFactory httpClientFactory) : Normalizer
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    [Description(ResourcesKeys.Desc_HpptNormalizer_Url_Description)]
    public required string Url { get; init; }

    [Description(ResourcesKeys.Desc_HpptNormalizer_Headers_Description)]
    public IEnumerable<Header> Headers { get; init; } = [];

    protected override async Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Iniciando llamada HTTP {RuleId}", RuleId);
        ArgumentNullException.ThrowIfNull(context.NewValue);

        if (string.IsNullOrWhiteSpace(Url))
        {
            Logger.LogError("URL property is not configured. {Rule}", RuleId);
            return NormalizerResult.Fail(this, context, "URL property is not configured.");
        }

        try
        {
            HttpClient client = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, Url);

            foreach (Header header in Headers)
            {
                request.Headers.Add(header.Name, header.Value);
            }

            request.Content = new StringContent(JsonConvert.SerializeObject(
                new NormalizedRequest
                {
                    Context = context,
                    MustNotifyChange = MustNotifyChange,
                    PropertyPath = PropertyPath
                }), Encoding.UTF8, "application/json");


            HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            _ = response.EnsureSuccessStatusCode();

            Logger.LogInformation("Llamada http realizada correctamente. Regla: {RuleId}", RuleId);
            string responseContent = "";
            try
            {
                responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                NormalizedResponse? responseModel = JsonConvert.DeserializeObject<NormalizedResponse>(responseContent);
                if (responseModel == null)
                {
                    Logger.LogError("Error en contenido de respuesta. {Response}.", responseContent);
                    return NormalizerResult.Fail(this, context, $"Hppt Normalizer: An error occurred during normalization: Deseialization null value.");
                }
                if (!responseModel.Success)
                {
                    return NormalizerResult.Fail(this, context, $"{ErrorMessage ?? "Error en normalizaci�n."}. Error: {responseModel.ErrorMessage}");
                }
                context.NewValue = responseModel.NormalizedValue;
                return NormalizerResult.Ok(this, context, responseModel.HasChange);


            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error en contenido de respuesta. {Response}.", responseContent);
                return NormalizerResult.Fail(this, context, $"Hppt Normalizer: An error occurred during normalization: {ex.Message}");
            }


        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error en llamada http. Rule {RuleId}. Error: {Error}", RuleId, ex.Message);
            return NormalizerResult.Fail(this, context, $"Hppt Normalizer: An error occurred during normalization: {ex.Message}");
        }
    }
}

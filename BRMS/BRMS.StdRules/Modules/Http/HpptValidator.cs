namespace BRMS.StdRules.Modules.Http;

//[RuleName("HpptValidator")]
//[Description(ResourcesKeys.Desc_HpptValidator_Description)]
//[SupportedTypes(RuleInputType.String)]
//public class HpptValidator : Validator
//{
//    private readonly IHttpClientFactory _httpClientFactory;

//    [Description(ResourcesKeys.Desc_HpptValidator_Url_Description)]
//    public required string Url { get; init; }

//    [Description(ResourcesKeys.Desc_HpptValidator_Headers_Description)]
//    public string? Headers { get; init; }

//    public HpptValidator(IHttpClientFactory httpClientFactory)
//    {
//        _httpClientFactory = httpClientFactory;
//    }

//    protected override async Task<RuleValidationResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation(LogMessageKeys.HpptValidator_Starting, context.RuleName);

//        var value = context.NewValue.GetValueAs<string>(PropertyPath);

//        if (string.IsNullOrWhiteSpace(Url))
//        {
//            Logger.LogError(LogMessageKeys.HpptValidator_Error, context.RuleName, "URL property is not configured.");
//            return RuleValidationResult.Error("URL property is not configured.");
//        }

//        try
//        {
//            var client = _httpClientFactory.CreateClient();
//            var request = new HttpRequestMessage(HttpMethod.Post, Url);

//            if (!string.IsNullOrWhiteSpace(Headers))
//            {
//                var headerDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Headers);
//                foreach (var header in headerDictionary)
//                {
//                    request.Headers.Add(header.Key, header.Value);
//                }
//            }

//            request.Content = new StringContent(JsonConvert.SerializeObject(value), Encoding.UTF8, "application/json");

//            Logger.LogInformation(LogMessageKeys.HpptValidator_Processing, context.RuleName, Url);
//            var response = await client.SendAsync(request, cancellationToken);
//            response.EnsureSuccessStatusCode();

//            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
//            var validationResult = JsonConvert.DeserializeObject<RuleValidationResult>(responseContent);

//            Logger.LogInformation(LogMessageKeys.HpptValidator_Success, context.RuleName);
//            return validationResult;
//        }
//        catch (Exception ex)
//        {
//            Logger.LogError(ex, LogMessageKeys.HpptValidator_Error, context.RuleName, ex.Message);
//            return RuleValidationResult.Error($"Hppt Validator: An error occurred during validation: {ex.Message}");
//        }
//    }
//}

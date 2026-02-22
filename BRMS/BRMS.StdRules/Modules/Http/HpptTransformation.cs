namespace BRMS.StdRules.Modules.Http;

//[RuleName("HpptTransformation")]
//[Description(ResourcesKeys.Desc_HpptTransformation_Description)]
//[SupportedTypes(RuleInputType.String)]
//public class HpptTransformation : Transformation
//{
//    private readonly IHttpClientFactory _httpClientFactory;

//    [Description(ResourcesKeys.Desc_HpptTransformation_Url_Description)]
//    public required string Url { get; init; }

//    [Description(ResourcesKeys.Desc_HpptTransformation_Headers_Description)]
//    public string? Headers { get; init; }

//    public HpptTransformation(IHttpClientFactory httpClientFactory)
//    {
//        _httpClientFactory = httpClientFactory;
//    }

//    protected override async Task<TransformationResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
//    {
//        Logger.LogInformation(LogMessageKeys.HpptTransformation_Starting, context.RuleName);

//        var value = context.NewValue.GetValueAs<string>(PropertyPath);

//        if (string.IsNullOrWhiteSpace(Url))
//        {
//            Logger.LogError(LogMessageKeys.HpptTransformation_Error, context.RuleName, "URL property is not configured.");
//            return TransformationResult.Fail(this, context, "URL property is not configured.");
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

//            Logger.LogInformation(LogMessageKeys.HpptTransformation_Processing, context.RuleName, Url);
//            var response = await client.SendAsync(request, cancellationToken);
//            response.EnsureSuccessStatusCode();

//            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
//            var transformedValue = JsonConvert.DeserializeObject<object>(responseContent);

//            context.NewValue.SetValueWithType(PropertyPath, transformedValue);

//            Logger.LogInformation(LogMessageKeys.HpptTransformation_Success, context.RuleName);
//            return new TransformationResult(this, context);
//        }
//        catch (Exception ex)
//        {
//            Logger.LogError(ex, LogMessageKeys.HpptTransformation_Error, context.RuleName, ex.Message);
//            return TransformationResult.Fail(this, context, $"Hppt Transformation: An error occurred during transformation: {ex.Message}");
//        }
//    }
//}

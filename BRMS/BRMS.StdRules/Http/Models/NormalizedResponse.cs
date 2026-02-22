using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Http.Models;

internal class NormalizedResponse
{
    public JObject? NormalizedValue { get; set; }
    public bool Success { get; set; }
    public bool? HasChange { get; set; }
    public string? ErrorMessage { get; set; }
}

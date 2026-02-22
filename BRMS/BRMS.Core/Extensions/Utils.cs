namespace BRMS.Core.Extensions;


public class CaseInsensitiveDictionary<T> : Dictionary<string, T>
{
    public CaseInsensitiveDictionary() : base(StringComparer.OrdinalIgnoreCase) { }
}

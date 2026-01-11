using AITaskAgent.Core.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Reflection;

namespace AITaskAgent.Support.JSON
{
    internal static class Serializations
    {
        public class IgnoreExceptionContractResolver : DefaultContractResolver
        {
            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var property = base.CreateProperty(member, memberSerialization);

                // If the property is an Exception or derives from it, always ignore it
                if (typeof(Exception).IsAssignableFrom(property.PropertyType))
                {
                    property.ShouldSerialize = _ => false;
                }

                return property;
            }
        }

        private static readonly JsonSerializerSettings _safeSettings = new()
        {
            ContractResolver = new IgnoreExceptionContractResolver(),
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
            // Prevents a casting error in a property from breaking the log
            Error = (sender, args) => { args.ErrorContext.Handled = true; }
        };

        public static JObject? WithoutExceptionProperties(this object error)
        {
            if (error == null) { return null; }
            // This captures all properties of the concrete class
            // but automatically removes ANY property of type Exception
            return JObject.FromObject(error, JsonSerializer.Create(_safeSettings));
        }

        public static object? WithoutExeption(this IStepError error)
        {

            return error == null || error.OriginalException == null ? error : error.WithoutExceptionProperties();
        }
    }
}

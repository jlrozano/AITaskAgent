using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AITaskAgent.Configuration
{
    public static partial class ConfigurationPatchExtensions
    {
        public static T? GetEx<T>(this IConfiguration configuration, string? key = null, IServiceProvider? sp = null) where T : class
        {
            IConfiguration section = string.IsNullOrEmpty(key) ? configuration : configuration.GetSection(key);

            if (section == null || ((section is IConfigurationSection sec) && !sec.Exists()))
            {
                return null;
            }

            JToken jToken = ReconstructSectionJson(section);

            // Evitar serialización si el tipo ya es correcto
            if (typeof(T) == typeof(JToken))
            {
                // Devuelves el token tal cual
                return (T)(object)jToken;
            }

            if (typeof(JToken).IsAssignableFrom(typeof(T)))
            {
                // Aquí T es JObject, JArray, JValue...
                // comprobar que el token realmente es de ese tipo
                return jToken.GetType() == typeof(T)
                ? (T)(object)jToken
                : throw new InvalidCastException(
                $"El JToken es de tipo {jToken.GetType().Name}, no puede convertirse a {typeof(T).Name}");
            }

            JsonSerializerSettings settings = new()
            {
                NullValueHandling = NullValueHandling.Ignore,
                ContractResolver = sp != null ? new DiContractResolver(sp) : null
            };

            return jToken.ToObject<T>(JsonSerializer.Create(settings));
        }
        /// <summary>
        /// Vincula la configuración a un objeto existente
        /// </summary>
        public static void BindEx<T>(this IConfigurationSection section, T instance, IServiceProvider? sp = null) where T : class
        {
            ArgumentNullException.ThrowIfNull(instance, nameof(instance));

            JToken jToken = ReconstructSectionJson(section);

            if (jToken.Type != JTokenType.Null)
            {

                JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    ContractResolver = sp != null ? new DiContractResolver(sp) : null
                });

                using JsonReader reader = jToken.CreateReader();
                serializer.Populate(reader, instance);

            }
        }

        private static JToken ReconstructSectionJson(IConfiguration configuration)
        {
            if (configuration is IConfigurationSection section && !string.IsNullOrEmpty(section.Value))
            {
                return ParsePrimitiveOrString(section.Value);
            }

            List<IConfigurationSection> children = configuration.GetChildren().ToList();

            if (children.Count == 0)
            {
                return JValue.CreateNull();
            }


            List<(IConfigurationSection Child, int? Index)> parsedKeys = new(children.Count);
            bool allNumeric = true;

            foreach (IConfigurationSection? child in children)
            {
                int? index = int.TryParse(child.Key, out int idx) ? idx : null;
                parsedKeys.Add((child, index));
                if (index == null)
                {
                    allNumeric = false;
                }
            }

            if (allNumeric)
            {
                JArray array = [];
                // Ordenar usando índices pre-parseados
                parsedKeys.Sort((a, b) => a.Index!.Value.CompareTo(b.Index!.Value));

                foreach ((IConfigurationSection Child, int? Index) item in parsedKeys)
                {
                    array.Add(ReconstructSectionJson(item.Child));
                }
                return array;
            }
            else
            {
                JObject obj = [];
                foreach ((IConfigurationSection Child, int? Index) item in parsedKeys)
                {
                    obj[item.Child.Key] = ReconstructSectionJson(item.Child);
                }
                return obj;
            }
        }

        private static JValue ParsePrimitiveOrString(string? value)
        {
            if (value == null)
            {
                return JValue.CreateNull();
            }
            string s = value.Trim();
            if (s.Length == 0)
            {
                return new JValue(string.Empty);
            }

            if (bool.TryParse(s, out bool b))
            {
                return new JValue(b);
            }
            // Enteros (sin punto decimal)
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            {
                return l is <= int.MaxValue and >= int.MinValue ? new JValue((int)l) : new JValue(l);
            }
            // Decimales exactos (tiene punto decimal)
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal m))
            {
                return new JValue(m);
            }
            // Números reales (notación científica, etc.)
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out double d))
            {
                return new JValue(d);
            }

            // Fechas y TimeSpan se quedan como string: Newtonsoft los convierte al enlazar una propiedad DateTime,
            // DateTimeOffset o TimeSpan, y una propiedad string (un ApiVersion "2024-10-21") recibe el valor intacto
            // en vez de la fecha reformateada con la cultura y la zona de la máquina.
            // Todo lo demás es string (con expansión de variables de entorno)
            return new JValue(ExpandEnvVars(value));
        }

        private static string ExpandEnvVars(string value)
        {
            return string.IsNullOrEmpty(value)
                ? value
                : MyRegex().Replace(value, m =>
                Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
        }

        [GeneratedRegex(@"\${(\w+)}")]
        private static partial Regex MyRegex();
    }

    /// <summary>
    /// Extensiones para facilitar el registro en el DI con IOptions
    /// </summary>
    public static class ServiceCollectionConfigurationExtensions
    {
        public static IServiceCollection ConfigureEx<T>(
            this IServiceCollection services, string sectionName) where T : class, new()
        {
            services.AddOptions();

            // En lugar de registrar IOptions<T> explícitamente (lo que sobrescribe el comportamiento por defecto 
            // de IOptionsManager e impide Validaciones/PostConfigure), dejamos que la inyección por 
            // defecto de .AddOptions() la resuelva, la cual automáticamente llamará a IConfigureOptions más abajo.
            /*
            services.TryAddSingleton<IOptions<T>>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                var instance = new T();
                configuration.GetSection(sectionName).BindEx(instance, sp);
                return Options.Create(instance);
            });
            */

            // CAMBIO AQUÍ: Usamos AddSingleton en lugar de TryAddEnumerable
            services.AddSingleton<IConfigureOptions<T>>(sp =>
            {
                IConfiguration configuration = sp.GetRequiredService<IConfiguration>();
                return new ConfigureNamedOptions<T>(Options.DefaultName, options =>
                {
                    configuration.GetSection(sectionName).BindEx(options, sp);
                });
            });

            // CAMBIO AQUÍ: Usamos AddSingleton en lugar de TryAddEnumerable
            services.AddSingleton<IOptionsChangeTokenSource<T>>(sp =>
            {
                IConfiguration configuration = sp.GetRequiredService<IConfiguration>();
                return new ConfigurationChangeTokenSource<T>(Options.DefaultName, configuration.GetSection(sectionName));
            });

            return services;
        }

        public static IServiceCollection ConfigureEx<T>(
            this IServiceCollection services, IConfigurationSection section) where T : class, new()
        {
            services.AddOptions();

            /*
            services.TryAddSingleton<IOptions<T>>(sp =>
            {
                var instance = new T();
                section.BindEx(instance, sp);
                return Options.Create(instance);
            });
            */

            // CAMBIO AQUÍ: Usamos AddSingleton para mantener consistencia
            services.AddSingleton<IConfigureOptions<T>>(sp =>
                new ConfigureNamedOptions<T>(Options.DefaultName, options =>
                {
                    section.BindEx(options, sp);
                }));

            // CAMBIO AQUÍ: Usamos AddSingleton para mantener consistencia
            services.AddSingleton<IOptionsChangeTokenSource<T>>(
                new ConfigurationChangeTokenSource<T>(Options.DefaultName, section));

            return services;
        }
    }


}

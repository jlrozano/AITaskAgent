using System.Text;
using System.Text.RegularExpressions;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// Entrada del paso que nombra los subsistemas: un único texto con el resumen de cada cluster, que la plantilla
/// (<c>domain_naming_prompt.md</c>) recibe como <c>{{ input }}</c>. Va como texto y no como propiedades tipadas por la misma
/// razón que <see cref="Models.VerificationInput"/>: una lista se pintaría como JSON en la plantilla.
/// </summary>
public sealed class DomainNamingInput : StepResult<string>
{
    public DomainNamingInput(string text)
        : base(new EmptyStep(nameof(DomainNamingInput)), text)
    {
    }
}

/// <summary>Lo que el LLM dijo de un subsistema, ya validado.</summary>
public sealed record SubsystemNaming(string Name, string Description, SubsystemPriority Priority);

/// <summary>
/// Nombrado de los subsistemas con el LLM y validación de su respuesta en código (T11, spec §4.2). El LLM solo pone nombre,
/// descripción corta y prioridad a clusters que ya existen: <b>no puede descartar ni inventar subsistemas</b>. Lo que no responde
/// de forma coherente (un id que no existe, un nombre vacío, repetido o con aspecto de plantilla, texto sin el formato pedido)
/// se ignora, y ese subsistema conserva el nombre derivado de su término dominante o su carpeta. Un fallo del LLM es un aviso y
/// el mapa sale con nombres derivados.
/// </summary>
/// <remarks>
/// El formato de la respuesta es texto plano (decisión vigente: sin JSON entre pasos): un bloque por subsistema que empieza con
/// <c>### &lt;id&gt;</c> y sigue con las líneas <c>Nombre:</c>, <c>Descripción:</c> y <c>Prioridad:</c>. El analizador tolera
/// lo habitual (negritas, viñetas, <c>Name</c>/<c>Description</c>/<c>Priority</c> en inglés, tildes ausentes).
/// </remarks>
public static class DomainNaming
{
    /// <summary>Subsistemas por llamada al LLM: más resúmenes en una sola petición empeoran el seguimiento del formato.</summary>
    public const int BatchSize = 40;

    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 400;

    private static readonly Regex Header = new(
        @"^\s*(?:#{1,6}\s*|\*{0,2}subsistema\*{0,2}\s*:?\s*\*{0,2}\s*|\*{0,2}id\*{0,2}\s*:\s*\*{0,2}\s*)[`*""']*(?<id>[A-Za-z0-9][A-Za-z0-9._-]*)[`*""']*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Field = new(
        @"^\s*[-*•]?\s*\*{0,2}(?<key>nombre|name|descripci[oó]n|description|prioridad|priority)\*{0,2}\s*:\s*\*{0,2}\s*(?<value>.*?)\s*\*{0,2}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// El resumen compacto de los subsistemas que va en la petición: id, tamaño, término dominante, carpetas, tipos clave,
    /// entradas y tablas. Nada de esto es el código, solo lo que hace falta para ponerles nombre.
    /// </summary>
    public static string BuildPrompt(IReadOnlyList<Subsystem> subsystems)
    {
        var text = new StringBuilder();
        foreach (var subsystem in subsystems)
        {
            text.Append("### ").AppendLine(subsystem.Id);
            text.Append("Tamaño: ").Append(subsystem.Metrics.Files).Append(" ficheros, ").Append(subsystem.Metrics.Loc).AppendLine(" líneas");
            if (subsystem.DominantTerm.Length > 0)
                text.Append("Término dominante: ").AppendLine(subsystem.DominantTerm);
            if (subsystem.Roots.Count > 0)
                text.Append("Carpetas: ").AppendLine(string.Join(", ", subsystem.Roots));
            if (subsystem.KeyTypes.Count > 0)
                text.Append("Tipos clave: ").AppendLine(string.Join(", ", subsystem.KeyTypes.Select(ShortName)));
            if (subsystem.EntryPoints.Count > 0)
                text.Append("Entradas: ").AppendLine(string.Join(", ", subsystem.EntryPoints.Take(6).Select(e => $"{e.Kind} {e.Name}")));
            if (subsystem.Tables.Count > 0)
                text.Append("Tablas: ").AppendLine(string.Join(", ", subsystem.Tables.Take(8)));
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    private static string ShortName(string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        return dot >= 0 ? qualified[(dot + 1)..] : qualified;
    }

    /// <summary>
    /// Lo que la respuesta dice de cada subsistema de <paramref name="knownIds"/>, validado. Un id desconocido, un bloque sin un
    /// nombre aceptable o un id repetido (vale el primero) no entran; un nombre ya usado por otro subsistema (sin mirar
    /// mayúsculas) tampoco.
    /// </summary>
    public static Dictionary<string, SubsystemNaming> Parse(string? response, IReadOnlyCollection<string> knownIds)
    {
        var result = new Dictionary<string, SubsystemNaming>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(response))
            return result;

        var known = new HashSet<string>(knownIds, StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? id = null;
        string name = "", description = "", priority = "";

        void Close()
        {
            if (id != null && known.Contains(id) && !result.ContainsKey(id) && AcceptableName(name) is { } accepted && usedNames.Add(accepted))
            {
                result[id] = new SubsystemNaming(accepted, Clean(description, MaxDescriptionLength), ParsePriority(priority));
            }
            id = null;
            name = description = priority = "";
        }

        foreach (var line in response.Replace("\r\n", "\n").Split('\n'))
        {
            var header = Header.Match(line);
            if (header.Success)
            {
                Close();
                id = header.Groups["id"].Value;
                continue;
            }

            // Otro encabezado que no es un identificador («### Notas»): cierra el bloque en curso para que sus líneas no se le atribuyan.
            if (line.TrimStart().StartsWith('#'))
            {
                Close();
                continue;
            }

            var field = Field.Match(line);
            if (!field.Success || id == null)
                continue;

            var value = field.Groups["value"].Value;
            switch (field.Groups["key"].Value.ToLowerInvariant())
            {
                case "nombre" or "name":
                    name = value;
                    break;
                case "prioridad" or "priority":
                    priority = value;
                    break;
                default:
                    description = value;
                    break;
            }
        }

        Close();
        return result;
    }

    /// <summary>El nombre sin comillas ni adornos si es aceptable (no vacío, no largo, sin aspecto de plantilla sin rellenar), o null.</summary>
    private static string? AcceptableName(string raw)
    {
        var name = Clean(raw, int.MaxValue).Trim('"', '\'', '`', '*', '_', ' ');
        if (name.Length < 2 || name.Length > MaxNameLength)
            return null;
        if (name.Contains('<') || name.Contains('>') || name.Contains('\n'))
            return null;
        return name;
    }

    private static string Clean(string text, int maxLength)
    {
        var clean = Regex.Replace(text.Trim(), @"\s+", " ");
        return clean.Length > maxLength ? clean[..maxLength].TrimEnd() + "…" : clean;
    }

    private static SubsystemPriority ParsePriority(string raw) => raw.Trim().Trim('*', '`', '.', ' ').ToLowerInvariant() switch
    {
        "alta" or "high" => SubsystemPriority.High,
        "media" or "medium" or "medio" => SubsystemPriority.Medium,
        "baja" or "low" or "bajo" => SubsystemPriority.Low,
        _ => SubsystemPriority.Unspecified
    };

    /// <summary>
    /// Aplica lo validado: los subsistemas con respuesta pasan a nombre del LLM; el resto conserva el derivado. Si un nombre
    /// derivado choca con uno del LLM (sin mirar mayúsculas), el derivado se distingue con su identificador.
    /// </summary>
    public static DomainMap Apply(DomainMap map, IReadOnlyDictionary<string, SubsystemNaming> naming)
    {
        var llmNames = new HashSet<string>(naming.Values.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
        var subsystems = map.Subsystems.Select(s =>
        {
            if (naming.TryGetValue(s.Id, out var named))
            {
                return s with
                {
                    Name = named.Name,
                    Description = named.Description,
                    Priority = named.Priority,
                    NameOrigin = SubsystemNameOrigins.Llm
                };
            }
            return llmNames.Contains(s.Name) ? s with { Name = $"{s.Name} ({s.Id})" } : s;
        }).ToList();
        return map with { Subsystems = subsystems };
    }

    /// <summary>
    /// Nombra los subsistemas de <paramref name="map"/> en tandas de <see cref="BatchSize"/> preguntando con
    /// <paramref name="ask"/> (el texto del resumen; devuelve la respuesta del LLM o null). Una tanda que falla o devuelve
    /// algo inservible es un aviso en <paramref name="warnings"/> y esos subsistemas siguen con su nombre derivado; solo la
    /// cancelación se propaga.
    /// </summary>
    public static async Task<DomainMap> NameAsync(
        DomainMap map, Func<string, CancellationToken, Task<string?>> ask, List<string> warnings, CancellationToken ct)
    {
        var naming = new Dictionary<string, SubsystemNaming>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // La infraestructura compartida lleva su nombre y su identificador fijos: no es una capacidad que bautizar.
        var nameable = map.Subsystems.Where(s => !s.IsShared).ToList();
        for (var start = 0; start < nameable.Count; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = nameable.Skip(start).Take(BatchSize).ToList();
            string? response;
            try
            {
                response = await ask(BuildPrompt(batch), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                warnings.Add($"⚠️ El LLM no pudo nombrar {batch.Count} subsistema(s): {ex.Message} Se usan nombres derivados del código.");
                continue;
            }

            var parsed = Parse(response, [.. batch.Select(s => s.Id)]);
            if (parsed.Count == 0)
            {
                warnings.Add($"⚠️ La respuesta del LLM al nombrar {batch.Count} subsistema(s) no se pudo usar: se usan nombres derivados del código.");
                continue;
            }

            foreach (var (id, value) in parsed.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                // Dos tandas pueden repetir un nombre: el primero (por id) se queda y el otro conserva el derivado.
                if (usedNames.Add(value.Name))
                    naming[id] = value;
            }
            if (parsed.Count < batch.Count)
                warnings.Add($"El LLM nombró {parsed.Count} de {batch.Count} subsistema(s); el resto conserva el nombre derivado del código.");
        }

        return Apply(map, naming);
    }
}

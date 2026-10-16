using AITaskAgent.LLM.Configuration;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Chat;

/// <summary>
/// Un modelo que se puede elegir para responder en el chat: una variante del rol del chat (su proveedor, modelo e
/// <c>Inference</c> con las <c>Options</c> propias del modelo) con la etiqueta que ve el usuario.
/// </summary>
/// <param name="Id">Identificador estable que guarda la sesión (<see cref="ChatSession.ModelId"/>).</param>
/// <param name="Label">Texto visible en el selector y en cada respuesta.</param>
/// <param name="ProviderName">Tipo de servicio del proveedor (<c>azure-openai</c>…).</param>
/// <param name="Model">Deployment o modelo del proveedor.</param>
/// <param name="Streaming">True si el deployment suelta la respuesta a medida que se genera (informativo para la UI).</param>
/// <param name="InputPricePerMillion">USD por millón de tokens de entrada; null si el modelo no tiene precio.</param>
/// <param name="OutputPricePerMillion">USD por millón de tokens de salida; null si el modelo no tiene precio.</param>
/// <param name="RoleName">Rol de <c>AITaskAgent:LlmProviders:Roles</c> del que sale.</param>
/// <param name="Role">El rol con el que corre el turno.</param>
public sealed record ChatModelOption(
    string Id,
    string Label,
    string ProviderName,
    string Model,
    bool Streaming,
    decimal? InputPricePerMillion,
    decimal? OutputPricePerMillion,
    string RoleName,
    LlmRole Role);

/// <summary>
/// Los modelos seleccionables del chat. Se declaran en <c>CodebaseAnalysis:Chat:Models</c> como una lista de
/// <c>{ Id, Label, Role, Streaming }</c> donde <c>Role</c> es un rol de <c>AITaskAgent:LlmProviders:Roles</c> (con su
/// proveedor, <c>Model</c> e <c>Inference</c>: cada modelo lleva sus propias <c>Options</c>), y
/// <c>CodebaseAnalysis:Chat:DefaultModel</c> es el id del modelo de las sesiones nuevas. Sin lista, el rol
/// <see cref="ChatAgent.RoleName"/> es la única opción. Nada de aquí aborta el arranque: lo incoherente se avisa y se omite.
/// </summary>
public sealed class ChatModelCatalog
{
    public const string ModelsSection = ChatOptions.SectionName + ":Models";
    public const string DefaultModelKey = ChatOptions.SectionName + ":DefaultModel";

    private ChatModelCatalog(IReadOnlyList<ChatModelOption> models, ChatModelOption @default)
    {
        Models = models;
        Default = @default;
    }

    public IReadOnlyList<ChatModelOption> Models { get; }

    /// <summary>El modelo de las sesiones nuevas y de las que no tienen modelo (o el suyo ya no está).</summary>
    public ChatModelOption Default { get; }

    /// <summary>
    /// El modelo de <paramref name="id"/>. Si es null o vacío (sesión antigua, sin campo) devuelve el de por defecto sin
    /// más; si no existe en la configuración (retirado), devuelve el de por defecto y avisa por <paramref name="warn"/>.
    /// </summary>
    public ChatModelOption Resolve(string? id, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Default;

        var found = Find(id);
        if (found != null)
            return found;

        warn?.Invoke($"El modelo de chat '{id}' ya no está en {ModelsSection}: se usa el de por defecto ('{Default.Id}').");
        return Default;
    }

    public ChatModelOption? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static ChatModelCatalog Build(IConfiguration configuration, IReadOnlyDictionary<string, LlmRole> roles, Action<string> warn)
    {
        var chatRole = roles.TryGetValue(ChatAgent.RoleName, out var r)
            ? r
            : throw new InvalidOperationException($"No hay rol '{ChatAgent.RoleName}': pídelo en requiredRoles al construir los roles.");

        var models = new List<ChatModelOption>();
        var entries = configuration.GetSection(ModelsSection).GetChildren().ToList();
        foreach (var entry in entries)
        {
            var roleName = entry["Role"];
            if (string.IsNullOrWhiteSpace(roleName) || !roles.TryGetValue(roleName, out var role))
            {
                warn($"⚠️ {ModelsSection}:{entry.Key}: el rol '{roleName}' no existe en AITaskAgent:LlmProviders:Roles; el modelo se omite.");
                continue;
            }

            var id = string.IsNullOrWhiteSpace(entry["Id"]) ? role.Model : entry["Id"]!.Trim();
            if (models.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                warn($"⚠️ {ModelsSection}: el id '{id}' está repetido; se omite la entrada {entry.Key}.");
                continue;
            }

            models.Add(Option(id, entry["Label"], roleName, role, entry.GetValue<bool?>("Streaming"), warn));
        }

        if (models.Count == 0)
        {
            if (entries.Count > 0)
                warn($"⚠️ {ModelsSection} no tiene ninguna entrada válida: el chat usa solo el rol '{ChatAgent.RoleName}'.");
            models.Add(Option(chatRole.Model, null, ChatAgent.RoleName, chatRole, null, warn));
        }

        var defaultId = configuration[DefaultModelKey];
        var @default = string.IsNullOrWhiteSpace(defaultId) ? null : models.FirstOrDefault(m => string.Equals(m.Id, defaultId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(defaultId) && @default == null)
            warn($"⚠️ {DefaultModelKey}='{defaultId}' no está entre los modelos del chat: se usa el del rol '{ChatAgent.RoleName}'.");

        @default ??= models.FirstOrDefault(m => m.RoleName == ChatAgent.RoleName) ?? models[0];
        return new ChatModelCatalog(models, @default);
    }

    private static ChatModelOption Option(string id, string? label, string roleName, LlmRole role, bool? streaming, Action<string> warn)
    {
        var useStreaming = role.Inference?.UseStreaming ?? role.Provider.Inference?.UseStreaming ?? false;
        if (streaming == true && !useStreaming)
            warn($"⚠️ El modelo de chat '{id}' declara Streaming pero su rol '{roleName}' no tiene Inference:UseStreaming.");

        var price = role.Provider.Models.FirstOrDefault(m => string.Equals(m.Name, role.Model, StringComparison.OrdinalIgnoreCase));
        if (price is not { InputPricePerMillion: not null, OutputPricePerMillion: not null })
            warn($"⚠️ El modelo '{role.Model}' del chat ('{id}') no tiene precio en Providers:<proveedor>:Models: su coste no se calcula.");

        return new ChatModelOption(
            id,
            string.IsNullOrWhiteSpace(label) ? id : label.Trim(),
            role.Provider.Service.Name,
            role.Model,
            streaming ?? useStreaming,
            price?.InputPricePerMillion,
            price?.OutputPricePerMillion,
            roleName,
            role);
    }
}

using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Notes;

/// <summary>
/// Sección <c>CodebaseAnalysis:NoteProtocol</c>: qué pasos llevan el protocolo de notas (<see cref="NoteTagHandler"/>): las
/// investigaciones por subsistema del modo B y los auditores (también la consolidación). Los dos activos por defecto.
/// </summary>
/// <remarks>Un valor que no es booleano cae al de por defecto: una errata no para el análisis.</remarks>
public sealed record NoteProtocolOptions
{
    public const string SectionName = "CodebaseAnalysis:NoteProtocol";

    public bool Investigations { get; init; } = true;

    public bool Auditors { get; init; } = true;

    /// <summary>
    /// Con tantos resultados <c>[Rn]</c> sin anotar de turnos anteriores, cada resultado nuevo lleva al final un recordatorio con
    /// los pendientes. 0 o menos: sin recordatorio.
    /// </summary>
    public int ReminderFromPending { get; init; } = 1;

    /// <summary>
    /// Con tantos resultados sin anotar de turnos anteriores, la herramienta no devuelve el dato nuevo (ni se ejecuta) hasta que
    /// el modelo anote los pendientes. 0 o menos: sin retención.
    /// </summary>
    public int WithholdFromPending { get; init; } = 12;

    /// <summary>
    /// Tras tantos turnos seguidos con consultas retenidas (sin una sola ejecutada entre medias), la herramienta pide al paso que
    /// corte el bucle de herramientas, y el paso termina por su respuesta final sin herramientas: un modelo que nunca anota y
    /// varía los argumentos no lo corta el detector de repeticiones. 0 o menos: sin corte.
    /// </summary>
    public int StopAfterWithheldTurns { get; init; } = 5;

    /// <summary>Ninguno de los dos: el pipeline queda como antes de existir el protocolo.</summary>
    public static NoteProtocolOptions Off { get; } = new() { Investigations = false, Auditors = false };

    public static NoteProtocolOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new NoteProtocolOptions();
        return new NoteProtocolOptions
        {
            Investigations = Bool(section, nameof(Investigations), defaults.Investigations),
            Auditors = Bool(section, nameof(Auditors), defaults.Auditors),
            ReminderFromPending = Int(section, nameof(ReminderFromPending), defaults.ReminderFromPending),
            WithholdFromPending = Int(section, nameof(WithholdFromPending), defaults.WithholdFromPending),
            StopAfterWithheldTurns = Int(section, nameof(StopAfterWithheldTurns), defaults.StopAfterWithheldTurns)
        };
    }

    /// <summary>Las de la configuración del <paramref name="serviceProvider"/>, o las de por defecto si no la hay.</summary>
    public static NoteProtocolOptions FromServices(IServiceProvider? serviceProvider) =>
        serviceProvider?.GetService(typeof(IConfiguration)) is IConfiguration configuration
            ? FromConfiguration(configuration)
            : new NoteProtocolOptions();

    private static int Int(IConfigurationSection section, string key, int fallback) =>
        section[key] is { Length: > 0 } raw
        && int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool Bool(IConfigurationSection section, string key, bool fallback) =>
        section[key] is { Length: > 0 } raw && bool.TryParse(raw.Trim(), out var value) ? value : fallback;
}

using CodebaseAnalysis.Core.Progress;
using Serilog.Core;
using Serilog.Events;

namespace Samples.CodebaseAnalysisPipeline;

/// <summary>
/// Añade a cada evento la propiedad <c>Origin</c>, <c>"[paso] "</c> o vacía, con el paso más interno de los scopes del pipeline
/// (<see cref="LogScopeOrigin"/>). Las plantillas de consola y fichero la ponen delante del mensaje, de modo que
/// «Executing Tool: view_file…» dice qué auditor llama a la tool aunque los nueve corran en paralelo.
/// </summary>
internal sealed class StepOriginEnricher : ILogEventEnricher
{
    public const string PropertyName = "Origin";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var step = logEvent.Properties.TryGetValue("Scope", out var scope) && scope is SequenceValue scopes
            ? LogScopeOrigin.InnermostStep(scopes.Elements.Select(e => e is ScalarValue { Value: var value } ? value?.ToString() : e.ToString()))
            : null;
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, step is null ? "" : $"[{step}] "));
    }
}

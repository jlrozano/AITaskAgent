using AITaskAgent.Support.Template;
using System.Text;

namespace CodebaseAnalysis.Core.Support;

public class AnalysisTemplateProvider : ITemplateProvider
{
    private readonly string _folderPath;
    private readonly AnalysisTemplateRenderer _renderer;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public AnalysisTemplateProvider(string folderPath)
    {
        _folderPath = Path.GetFullPath(folderPath);
        _renderer = new AnalysisTemplateRenderer(_folderPath);
    }

    /// <summary>Devuelve null si no existe, como exige <see cref="ITemplateProvider"/>.</summary>
    public string? GetTemplate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(name, out var cached))
                return cached;

            var content = LoadFromFile(name);
            if (content != null)
            {
                _cache[name] = content;
            }

            return content;
        }
    }

    private string? LoadFromFile(string name)
    {
        var candidates = new List<string>();

        if (Path.IsPathRooted(name))
        {
            candidates.Add(name);
            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                candidates.Add(name + ".md");
        }
        else
        {
            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(_folderPath, name + ".md"));
            }
            candidates.Add(Path.Combine(_folderPath, name));
        }

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
        }

        return null;
    }

    /// <summary>
    /// Prompt y SystemPrompt son siempre nombres de plantilla. Uno que no existe es un error de
    /// configuración y lanza: devolver null tampoco sirve, porque <c>PromptTemplateResolver</c> cae
    /// entonces al texto crudo y el LLM recibiría el nombre de la plantilla como prompt.
    /// </summary>
    public string? Render(string name, object model)
    {
        var templateContent = GetTemplate(name)
            ?? throw new FileNotFoundException($"No existe la plantilla '{name}' en '{_folderPath}'.", name);

        return _renderer.Render(templateContent, model);
    }
}

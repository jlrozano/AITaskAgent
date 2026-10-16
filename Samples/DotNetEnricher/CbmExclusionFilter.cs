using System.Text;
using System.Text.RegularExpressions;

namespace DotNetEnricher;

/// <summary>
/// Replica la cadena de exclusión de codebase-memory-mcp:
///   hardcoded ALWAYS_SKIP → .gitignore → .cbmignore
///
/// Los enriquecedores Roslyn filtran compilation.SyntaxTrees con esta clase para omitir
/// exactamente los ficheros que el indexador no procesó.
/// </summary>
public class CbmExclusionFilter
{
    // ─── Patrones hardcodeados (ALWAYS_SKIP de codebase-memory) ─────────────────

    private static readonly HashSet<string> AlwaysSkipDirSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", "dist", "node_modules", ".vs", ".vscode", ".idea", ".git",
        "temp", "tmp", "target", "vendor", "bower_components", ".next", ".nuxt",
        ".angular", ".parcel-cache", ".turbo", "coverage", "htmlcov",
        ".cache", ".eggs", ".mypy_cache", ".pytest_cache",
        "__pycache__", ".npm", ".yarn", ".terraform", ".serverless",
        "bazel-bin", "bazel-out", "bazel-testlogs"
    };

    private static readonly HashSet<string> AlwaysSkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".so", ".a", ".o", ".bin", ".dat",
        ".db", ".sqlite", ".sqlite3",
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".tiff", ".webp", ".svg",
        ".wasm", ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".class", ".pyc", ".pyo"
    };

    private static readonly string[] GeneratedFileSuffixes =
    [
        ".Designer.cs", ".g.cs", ".g.i.cs", ".generated.cs", ".AssemblyInfo.cs"
    ];

    private static readonly string[] AlwaysSkipSubstrings =
    [
        ".generated.", "TemporaryGeneratedFile_"
    ];

    // ─── Patrones dinámicos de .gitignore / .cbmignore ──────────────────────────

    private readonly string _repoRoot;
    private readonly List<IgnorePattern> _patterns = [];

    public CbmExclusionFilter(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            _repoRoot = "";
            return;
        }

        _repoRoot = repoRoot.Replace('\\', '/').TrimEnd('/');
        LoadIgnoreFile(Path.Combine(repoRoot, ".gitignore"));
        LoadIgnoreFile(Path.Combine(repoRoot, ".cbmignore"));
    }

    // ─── API pública ─────────────────────────────────────────────────────────────

    public bool ShouldExclude(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return true;

        var normalized = filePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        var ext = Path.GetExtension(fileName);

        // Las reglas se aplican a la ruta relativa al repositorio, como hace cbm. Con la absoluta, un repositorio
        // bajo una carpeta Temp, tmp, bin… (p. ej. %TEMP%) quedaba excluido entero y no se enriquecía nada.
        // Un fichero fuera del repositorio tampoco lo indexa cbm.
        var prefix = _repoRoot + "/";
        if (_repoRoot.Length > 0 && !normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return true;
        var relativePath = _repoRoot.Length > 0 ? normalized[prefix.Length..] : normalized;

        // 1. Extensión siempre ignorada
        if (!string.IsNullOrEmpty(ext) && AlwaysSkipExtensions.Contains(ext))
            return true;

        // 2. Segmento de directorio siempre excluido
        var segments = relativePath.Split('/');
        foreach (var segment in segments[..^1])
        {
            if (AlwaysSkipDirSegments.Contains(segment))
                return true;
        }

        // 3. Fichero C# auto-generado
        foreach (var suffix in GeneratedFileSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 4. Substring en el path
        foreach (var sub in AlwaysSkipSubstrings)
        {
            if (relativePath.Contains(sub, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 5. Patrones dinámicos de .gitignore / .cbmignore — la ÚLTIMA coincidencia gana
        if (_patterns.Count == 0 || string.IsNullOrEmpty(_repoRoot)) return false;

        bool? excluded = null;
        foreach (var p in _patterns)
        {
            if (MatchesPattern(relativePath, p))
                excluded = !p.IsNegation;
        }

        return excluded == true;
    }

    // ─── Carga y parseo de ficheros ignore ───────────────────────────────────────

    private void LoadIgnoreFile(string path)
    {
        if (!File.Exists(path)) return;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            var isNegation = line.StartsWith('!');
            if (isNegation) line = line[1..];

            // Eliminar escape de backslash ante espacio o # (sintaxis gitignore)
            line = line.Replace(@"\ ", " ").Replace(@"\#", "#");

            var isAnchored = line.StartsWith('/');
            if (isAnchored) line = line[1..];

            var isDir = line.EndsWith('/');
            if (isDir) line = line[..^1];

            if (string.IsNullOrEmpty(line)) continue;

            // Un patrón que contiene '/' es implícitamente anclado a la raíz
            if (!isAnchored && line.Contains('/'))
                isAnchored = true;

            var regex = GlobToRegex(line);
            _patterns.Add(new IgnorePattern(regex, isNegation, isAnchored, isDir));
        }
    }

    // Convierte un glob de gitignore a regex preservando [...] character classes.
    private static string GlobToRegex(string pattern)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                sb.Append(".*");
                i += 2;
                if (i < pattern.Length && pattern[i] == '/') i++; // consumir /**/ → .*
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
                i++;
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else if (c == '[')
            {
                // Copiar el character class [...] tal cual — es regex válido directamente
                int j = i + 1;
                if (j < pattern.Length && pattern[j] == '!') j++; // [!...] → negated class
                if (j < pattern.Length && pattern[j] == ']') j++; // ] literal al inicio
                while (j < pattern.Length && pattern[j] != ']') j++;
                sb.Append(pattern[i..(j + 1)]);
                i = j + 1;
            }
            else if (IsRegexSpecial(c))
            {
                sb.Append('\\');
                sb.Append(c);
                i++;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    private static bool IsRegexSpecial(char c) =>
        c is '.' or '+' or '^' or '$' or '{' or '}' or '(' or ')' or '|' or '\\';

    // ─── Matching ────────────────────────────────────────────────────────────────

    private static bool MatchesPattern(string relativePath, IgnorePattern p)
    {
        try
        {
            // Anclado: debe coincidir desde el inicio del path relativo
            // No anclado: puede coincidir en cualquier segmento
            // IsDir: el patrón puede coincidir como prefijo de path (hay algo después)
            string regexStr = p.IsAnchored
                ? $"^{p.Pattern}(/|$)"
                : $"(^|/){p.Pattern}(/|$)";

            return Regex.IsMatch(relativePath, regexStr, RegexOptions.IgnoreCase);
        }
        catch (RegexParseException)
        {
            return false; // patrón inválido — ignorar de forma segura
        }
    }

    private record IgnorePattern(string Pattern, bool IsNegation, bool IsAnchored, bool IsDir);
}

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

/// <summary>
/// Las vistas de estructura de <c>graph_domain</c>: <c>projects</c> (proyectos C# con su acoplamiento, rol y dependencias
/// contrastadas con lo que declara el .csproj) y <c>contracts</c> (contratos frente a implementaciones, y de qué tipos de
/// otro proyecto depende un proyecto). Salen de lo que escribe el enriquecedor Roslyn: <c>CsProject</c>,
/// <c>PROJECT_DEPENDS_ON</c>, <c>BELONGS_TO_PROJECT</c> y, en cada tipo, <c>type_role</c>, <c>is_public</c> y <c>cs_project</c>.
/// </summary>
public sealed partial class GraphDomainTool
{
    /// <summary>Propiedades de un nodo con alias <paramref name="alias"/> si son JSON válido, y null si no.</summary>
    private static string SafeProps(string alias) => $"(CASE WHEN json_valid({alias}.properties) THEN {alias}.properties END)";

    private static readonly string[] TypeLabels = ["Class", "Interface", "Enum"];

    /// <summary>Un nodo <c>CsProject</c> con las propiedades que escribe el enriquecedor.</summary>
    private sealed record ProjectRow(
        string Name, string Directory, string Role, int? Ca, int? Ce, double? Instability, double? Abstractness, double? Distance,
        IReadOnlyDictionary<string, int> TypeRoles, IReadOnlyList<string> References, IReadOnlyList<string> Packages)
    {
        public bool DeclaresReferences => References.Count > 0;
    }

    private async Task<IReadOnlyList<ProjectRow>> LoadProjectsAsync(string? dbPath, CancellationToken ct)
    {
        var rows = await _graphDb.SelectAsync(
            $"SELECT n.name AS name, {SafeProps("n")} AS props FROM nodes n WHERE n.label = 'CsProject' ORDER BY n.name",
            null, dbPath, ct);

        var projects = new List<ProjectRow>();
        foreach (var r in rows)
        {
            JsonElement root = default;
            var hasProps = r.GetValueOrDefault("props") is string json && TryParse(json, out root);
            projects.Add(new ProjectRow(
                r.GetValueOrDefault("name")?.ToString() ?? "",
                hasProps ? Text(root, "directory") ?? "" : "",
                hasProps ? Text(root, "role") ?? "-" : "-",
                hasProps ? Int(root, "afferent_coupling") : null,
                hasProps ? Int(root, "efferent_coupling") : null,
                hasProps ? Number(root, "instability") : null,
                hasProps ? Number(root, "abstractness") : null,
                hasProps ? Number(root, "distance") : null,
                hasProps ? Counts(root, "type_roles") : new Dictionary<string, int>(),
                hasProps ? Strings(root, "project_references") : [],
                hasProps ? Strings(root, "packages") : []));
        }
        return projects;
    }

    private static bool TryParse(string json, out JsonElement root)
    {
        try
        {
            root = JsonDocument.Parse(json).RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static string? Text(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? Number(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static IReadOnlyList<string> Strings(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];

    private static IReadOnlyDictionary<string, int> Counts(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object
            ? v.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number).ToDictionary(p => p.Name, p => p.Value.GetInt32())
            : new Dictionary<string, int>();

    private static string Fmt(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-";

    private static string Fmt(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    /// <summary>Proyecto C# de un fichero: el de la carpeta más larga que lo contiene (como el enriquecedor). Null si ninguno.</summary>
    private static string? ProjectOfFile(IReadOnlyList<ProjectRow> projects, string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var path = filePath.Replace('\\', '/');
        ProjectRow? best = null;
        foreach (var p in projects)
        {
            var dir = p.Directory.Replace('\\', '/').Trim('/');
            var contains = dir.Length == 0 || path.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase);
            if (contains && (best is null || dir.Length > best.Directory.Trim('/').Length))
                best = p;
        }
        return best?.Name;
    }

    // ─── view: projects ─────────────────────────────────────────────────────────

    private async Task<string> DomainProjectsAsync(string? dbPath, string name, int limit, CancellationToken ct)
    {
        var projects = await LoadProjectsAsync(dbPath, ct);
        var sb = new StringBuilder();
        if (projects.Count == 0)
        {
            sb.AppendLine("# Proyectos C#\n");
            sb.AppendLine("*(No hay nodos CsProject en el grafo: el repositorio no es .NET o el enriquecimiento Roslyn no se ha ejecutado)*");
            return sb.ToString();
        }

        var selected = string.IsNullOrWhiteSpace(name)
            ? projects
            : [.. projects.Where(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))];
        if (selected.Count == 0)
        {
            sb.AppendLine($"# Proyectos C#\n\n*(No hay un proyecto llamado `{name}`. Proyectos del grafo: {string.Join(", ", projects.Select(p => $"`{p.Name}`"))})*");
            return sb.ToString();
        }

        var elements = (await _graphDb.SelectAsync(
            "SELECT t.name AS project, COUNT(*) AS n FROM edges e JOIN nodes t ON t.id = e.target_id " +
            "WHERE e.type = 'BELONGS_TO_PROJECT' AND t.label = 'CsProject' GROUP BY t.name", null, dbPath, ct))
            .ToDictionary(r => r.GetValueOrDefault("project")?.ToString() ?? "", r => Convert.ToInt32(r.GetValueOrDefault("n"), CultureInfo.InvariantCulture),
                StringComparer.OrdinalIgnoreCase);

        sb.AppendLine($"# Proyectos C# ({selected.Count})\n");
        sb.AppendLine("Ca = proyectos que dependen de él, Ce = proyectos de los que depende, I = Ce / (Ca + Ce), A = abstracción (interfaces y clases abstractas / tipos), D = distancia a la secuencia principal |A + I - 1|.\n");
        sb.AppendLine("| Proyecto | Rol | Ca | Ce | I | A | D | Elementos | Tipos (contrato / implementación / infraestructura / modelo) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var p in selected.Take(limit))
        {
            var roles = p.TypeRoles.Count == 0
                ? "-"
                : string.Join(" / ", new[] { "contract", "implementation", "infrastructure", "model" }.Select(k => p.TypeRoles.GetValueOrDefault(k)));
            sb.AppendLine($"| `{p.Name}` | {p.Role} | {Fmt(p.Ca)} | {Fmt(p.Ce)} | {Fmt(p.Instability)} | {Fmt(p.Abstractness)} | {Fmt(p.Distance)} | {elements.GetValueOrDefault(p.Name)} | {roles} |");
        }

        var packages = selected.Where(p => p.Packages.Count > 0).ToList();
        if (packages.Count > 0)
        {
            sb.AppendLine("\n## Paquetes NuGet declarados\n");
            foreach (var p in packages)
                sb.AppendLine($"- `{p.Name}`: {string.Join(", ", p.Packages.Select(x => $"`{x}`"))}");
        }

        await AppendProjectDependenciesAsync(sb, projects, selected, dbPath, limit, ct);
        return sb.ToString();
    }

    private async Task AppendProjectDependenciesAsync(
        StringBuilder sb, IReadOnlyList<ProjectRow> all, IReadOnlyList<ProjectRow> selected, string? dbPath, int limit, CancellationToken ct)
    {
        var edges = await _graphDb.SelectAsync(
            $"SELECT a.name AS from_proj, b.name AS to_proj, {CbmGraphDb.SafeEdgeProperties} AS props " +
            "FROM edges e JOIN nodes a ON a.id = e.source_id JOIN nodes b ON b.id = e.target_id " +
            "WHERE e.type = 'PROJECT_DEPENDS_ON' ORDER BY a.name, b.name", null, dbPath, ct);

        var names = selected.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byName = all.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        // Sin ningún ProjectReference en el grafo (enriquecimiento anterior) no se puede decir si una dependencia está declarada.
        var anyDeclared = all.Any(p => p.DeclaresReferences);
        var observed = new HashSet<(string, string)>();

        var rows = new List<string>();
        foreach (var r in edges)
        {
            var from = r.GetValueOrDefault("from_proj")?.ToString() ?? "";
            var to = r.GetValueOrDefault("to_proj")?.ToString() ?? "";
            observed.Add((from.ToLowerInvariant(), to.ToLowerInvariant()));
            if (!names.Contains(from) && !names.Contains(to)) continue;

            int? weight = null;
            var via = "-";
            var violation = false;
            if (r.GetValueOrDefault("props") is string json && TryParse(json, out var props))
            {
                weight = Int(props, "weight");
                var counts = Counts(props, "via");
                if (counts.Count > 0)
                    via = string.Join(", ", counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"));
                violation = props.TryGetProperty("layer_violation", out var v) && v.ValueKind == JsonValueKind.True;
            }

            var declared = !anyDeclared ? "sin datos"
                : byName.TryGetValue(from, out var source) && source.References.Contains(to, StringComparer.OrdinalIgnoreCase) ? "declarada" : "no declarada";
            rows.Add($"| `{from}` | `{to}` | {Fmt(weight)} | {via} | {declared}{(violation ? " · violación de capas" : "")} |");
        }

        sb.AppendLine($"\n## Dependencias entre proyectos observadas en el código ({rows.Count})\n");
        if (rows.Count == 0)
        {
            sb.AppendLine("*(No hay aristas PROJECT_DEPENDS_ON para esta selección)*");
        }
        else
        {
            sb.AppendLine("| Desde | Hacia | Peso | Vía (aristas) | ProjectReference |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var row in rows.Take(limit))
                sb.AppendLine(row);
            if (rows.Count > limit)
                sb.AppendLine($"\n*({rows.Count - limit} dependencias más; sube `limit` o filtra por `name`)*");
            if (anyDeclared)
                sb.AppendLine("\n«no declarada» = el código usa el otro proyecto sin `ProjectReference` directo: llega por una referencia transitiva o es una arista heurística de cbm (resuelta por nombre) que no es real; confírmalo en el código.");
        }

        if (!anyDeclared) return;
        var unused = selected
            .SelectMany(p => p.References.Select(target => (From: p.Name, To: target)))
            .Where(d => !observed.Contains((d.From.ToLowerInvariant(), d.To.ToLowerInvariant())))
            .ToList();
        if (unused.Count > 0)
        {
            sb.AppendLine("\n## ProjectReference declaradas sin dependencia observada\n");
            foreach (var (from, to) in unused)
                sb.AppendLine($"- `{from}` → `{to}`");
        }
    }

    // ─── view: contracts ────────────────────────────────────────────────────────

    /// <summary>Papeles de tipo que escribe <c>TypeRoleEnricher</c>, en el orden en que se muestran.</summary>
    private static readonly string[] TypeRoleOrder = ["contract", "implementation", "infrastructure", "model"];

    private async Task<string> DomainContractsAsync(string? dbPath, string name, int limit, CancellationToken ct)
    {
        var projects = await LoadProjectsAsync(dbPath, ct);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var project = projects.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (project is not null)
                return await ContractsOfProjectAsync(dbPath, projects, project, limit, ct);
            return await ContractsOfTypeAsync(dbPath, projects, name.Trim(), limit, ct);
        }
        return await ContractsOverviewAsync(dbPath, limit, ct);
    }

    /// <summary>Un tipo del grafo con lo que el enriquecedor sabe de él.</summary>
    private sealed record TypeRow(long Id, string Label, string Name, string Qn, string? FilePath, string? Role, bool? IsPublic, string? Project, string Relation, string? Lifetime);

    private static TypeRow ToTypeRow(IReadOnlyDictionary<string, object?> r)
    {
        string? role = null, project = null;
        bool? isPublic = null;
        if (r.GetValueOrDefault("props") is string json && TryParse(json, out var props))
        {
            role = Text(props, "type_role");
            project = Text(props, "cs_project");
            if (props.TryGetProperty("is_public", out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False)
                isPublic = p.GetBoolean();
        }
        return new TypeRow(
            Convert.ToInt64(r.GetValueOrDefault("id") ?? 0L, CultureInfo.InvariantCulture),
            r.GetValueOrDefault("label")?.ToString() ?? "",
            r.GetValueOrDefault("name")?.ToString() ?? "",
            r.GetValueOrDefault("qn")?.ToString() ?? "",
            r.GetValueOrDefault("file_path")?.ToString(),
            role, isPublic, project,
            r.GetValueOrDefault("rel")?.ToString() ?? "",
            r.GetValueOrDefault("lifetime")?.ToString());
    }

    private static string Visibility(bool? isPublic) => isPublic switch { true => "pública", false => "interna", _ => "-" };

    private static string Role(string? role) => role ?? "-";

    private async Task<string> ContractsOverviewAsync(string? dbPath, int limit, CancellationToken ct)
    {
        var sb = new StringBuilder("# Contratos frente a implementaciones\n\n");
        var perProject = await _graphDb.SelectAsync(
            $"SELECT coalesce(json_extract({SafeProps("n")}, '$.cs_project'), '(sin proyecto)') AS proj, " +
            $"json_extract({SafeProps("n")}, '$.type_role') AS role, " +
            "COUNT(*) AS n " +
            $"FROM nodes n WHERE n.label IN ('Class','Interface','Enum') AND json_extract({SafeProps("n")}, '$.type_role') IS NOT NULL " +
            "GROUP BY proj, role ORDER BY proj", null, dbPath, ct);

        if (perProject.Count == 0)
        {
            sb.AppendLine("*(Ningún tipo del grafo tiene `type_role`: hace falta el enriquecimiento Roslyn de versión 4 o posterior; vuelve a analizar el repositorio)*");
            return sb.ToString();
        }

        var table = perProject
            .GroupBy(r => r.GetValueOrDefault("proj")?.ToString() ?? "")
            .Select(g => (Project: g.Key,
                Roles: g.GroupBy(r => r.GetValueOrDefault("role")?.ToString() ?? "")
                    .ToDictionary(x => x.Key, x => x.Sum(r => Convert.ToInt32(r.GetValueOrDefault("n"), CultureInfo.InvariantCulture)))))
            .ToList();

        sb.AppendLine("Papel de cada tipo (`type_role`): contrato = interfaz o clase abstracta pública; implementación; infraestructura (estáticas, puntos de entrada, derivadas de clases del framework, abstracciones internas); modelo (enums, DTO, excepciones, atributos).\n");
        sb.AppendLine("| Proyecto | Contratos | Implementaciones | Infraestructura | Modelo |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var (project, roles) in table.Take(limit))
            sb.AppendLine($"| `{project}` | {string.Join(" | ", TypeRoleOrder.Select(k => roles.GetValueOrDefault(k)))} |");

        var contracts = await _graphDb.SelectAsync(
            "SELECT n.id AS id, n.name AS name, n.qualified_name AS qn, " +
            $"json_extract({SafeProps("n")}, '$.cs_project') AS project, " +
            "(SELECT COUNT(DISTINCT s.id) FROM edges e JOIN nodes s ON s.id = e.source_id " +
            " WHERE e.target_id = n.id AND e.type IN ('IMPLEMENTS','INHERITS')) AS impls, " +
            "(SELECT COUNT(1) FROM edges e WHERE e.target_id = n.id AND e.type = 'USAGE') AS uses " +
            $"FROM nodes n WHERE n.label IN ('Class','Interface') AND json_extract({SafeProps("n")}, '$.type_role') = 'contract' " +
            "ORDER BY impls DESC, uses DESC, n.name LIMIT @lim",
            p => p.AddWithValue("@lim", limit), dbPath, ct);

        sb.AppendLine("\n## Contratos con más implementaciones\n");
        if (contracts.Count == 0)
        {
            sb.AppendLine("*(No hay contratos públicos en el grafo)*");
        }
        else
        {
            sb.AppendLine("| Contrato | Proyecto | Implementaciones | Usos |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var c in contracts)
                sb.AppendLine($"| `{c.GetValueOrDefault("name")}` | `{c.GetValueOrDefault("project") ?? "-"}` | {c.GetValueOrDefault("impls")} | {c.GetValueOrDefault("uses")} |");
        }

        sb.AppendLine("\nPara el detalle de un contrato o clase pasa `name` (sus implementaciones y consumidores); con el nombre de un proyecto, qué tipos de otros proyectos usa y si son contratos o implementaciones concretas.");
        return sb.ToString();
    }

    private async Task<string> ContractsOfTypeAsync(string? dbPath, IReadOnlyList<ProjectRow> projects, string name, int limit, CancellationToken ct)
    {
        var matches = (await _graphDb.SelectAsync(
            "SELECT n.id AS id, n.label AS label, n.name AS name, n.qualified_name AS qn, n.file_path AS file_path, " +
            $"{SafeProps("n")} AS props FROM nodes n WHERE n.label IN ('Class','Interface','Enum') " +
            "AND (lower(n.name) = lower(@n) OR lower(n.qualified_name) = lower(@n) " +
            "OR lower(substr(n.qualified_name, -length(@n) - 1)) = '.' || lower(@n)) ORDER BY n.qualified_name",
            p => p.AddWithValue("@n", name), dbPath, ct)).Select(ToTypeRow).ToList();

        var sb = new StringBuilder();
        if (matches.Count == 0)
        {
            sb.AppendLine($"# Contratos frente a implementaciones\n\n*(No hay un proyecto ni un tipo llamado `{name}` en el grafo)*");
            return sb.ToString();
        }
        if (matches.Count > 1)
        {
            sb.AppendLine($"# Contratos frente a implementaciones\n\nHay {matches.Count} tipos que casan con `{name}`; repite con el Qualified Name exacto:\n");
            sb.AppendLine("| Tipo | Etiqueta | Papel | Proyecto | Qualified Name |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var m in matches.Take(limit))
                sb.AppendLine($"| `{m.Name}` | {m.Label} | {Role(m.Role)} | {m.Project ?? "-"} | `{m.Qn}` |");
            return sb.ToString();
        }

        var type = matches[0];
        var projectOfType = type.Project ?? ProjectOfFile(projects, type.FilePath) ?? "-";
        sb.AppendLine($"# `{type.Name}`: {Role(type.Role)} ({type.Label}, {Visibility(type.IsPublic)}, proyecto `{projectOfType}`)\n");
        sb.AppendLine($"- Qualified Name: `{type.Qn}`");

        var implementations = (await _graphDb.SelectAsync(
            "SELECT s.id AS id, s.label AS label, s.name AS name, s.qualified_name AS qn, s.file_path AS file_path, " +
            $"{SafeProps("s")} AS props, e.type AS rel, " +
            "(SELECT group_concat(DISTINCT coalesce(json_extract((CASE WHEN json_valid(di.properties) THEN di.properties END), '$.lifetime'), " +
            "json_extract((CASE WHEN json_valid(di.properties) THEN di.properties END), '$.di_lifetime'))) " +
            " FROM edges d JOIN nodes di ON di.id = d.source_id AND di.label = 'DiRegistration' " +
            " WHERE d.type = 'DI_RESOLVES' AND d.target_id = s.id) AS lifetime " +
            "FROM edges e JOIN nodes s ON s.id = e.source_id " +
            "WHERE e.target_id = @id AND e.type IN ('IMPLEMENTS','INHERITS') ORDER BY s.name",
            p => p.AddWithValue("@id", type.Id), dbPath, ct)).Select(ToTypeRow).ToList();

        var distinct = implementations
            .GroupBy(i => i.Id)
            .Select(g => g.First() with { Relation = string.Join(" + ", g.Select(x => x.Relation).Distinct().Order(StringComparer.Ordinal)) })
            .ToList();

        var verb = type.Label == "Interface" || type.Role == "contract" ? "Implementaciones y derivadas" : "Tipos derivados";
        sb.AppendLine($"\n## {verb} ({distinct.Count})\n");
        if (distinct.Count == 0)
        {
            sb.AppendLine("*(Ningún tipo del grafo la implementa o hereda de ella)*");
        }
        else
        {
            sb.AppendLine("| Tipo | Papel | Visibilidad | Proyecto | Relación | Lifetime DI |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var i in distinct.Take(limit))
                sb.AppendLine($"| `{i.Name}` | {Role(i.Role)} | {Visibility(i.IsPublic)} | {i.Project ?? ProjectOfFile(projects, i.FilePath) ?? "-"} | {i.Relation} | {i.Lifetime ?? "-"} |");
            if (distinct.Count > limit)
                sb.AppendLine($"\n*({distinct.Count - limit} más; sube `limit`)*");
        }

        // Consumidores: quien usa el tipo (USAGE) o llama a alguno de sus métodos (CALLS). De un método se muestra el tipo que lo declara.
        var consumers = await _graphDb.SelectAsync(
            "SELECT s.id AS id, s.name AS name, s.file_path AS file_path, e.type AS rel, " +
            "coalesce((SELECT o.name FROM edges dm JOIN nodes o ON o.id = dm.source_id WHERE dm.type = 'DEFINES_METHOD' AND dm.target_id = s.id LIMIT 1), s.name) AS owner, " +
            "coalesce((SELECT o.id FROM edges dm JOIN nodes o ON o.id = dm.source_id WHERE dm.type = 'DEFINES_METHOD' AND dm.target_id = s.id LIMIT 1), s.id) AS owner_id " +
            "FROM edges e JOIN nodes s ON s.id = e.source_id WHERE e.target_id = @id AND e.type = 'USAGE' " +
            "UNION " +
            "SELECT s.id, s.name, s.file_path, e.type, " +
            "coalesce((SELECT o.name FROM edges dm JOIN nodes o ON o.id = dm.source_id WHERE dm.type = 'DEFINES_METHOD' AND dm.target_id = s.id LIMIT 1), s.name), " +
            "coalesce((SELECT o.id FROM edges dm JOIN nodes o ON o.id = dm.source_id WHERE dm.type = 'DEFINES_METHOD' AND dm.target_id = s.id LIMIT 1), s.id) " +
            "FROM edges e JOIN nodes s ON s.id = e.source_id " +
            "JOIN edges m ON m.target_id = e.target_id AND m.type = 'DEFINES_METHOD' AND m.source_id = @id " +
            "WHERE e.type = 'CALLS'",
            p => p.AddWithValue("@id", type.Id), dbPath, ct);

        var grouped = consumers
            .Where(c => Convert.ToInt64(c.GetValueOrDefault("owner_id"), CultureInfo.InvariantCulture) != type.Id)
            .GroupBy(c => (Owner: c.GetValueOrDefault("owner")?.ToString() ?? "", Project: ProjectOfFile(projects, c.GetValueOrDefault("file_path")?.ToString()) ?? "-"))
            .Select(g => (g.Key.Owner, g.Key.Project, Rels: string.Join(", ", g.Select(c => c.GetValueOrDefault("rel")?.ToString()).Distinct().Order(StringComparer.Ordinal))))
            .OrderBy(g => g.Project, StringComparer.Ordinal).ThenBy(g => g.Owner, StringComparer.Ordinal)
            .ToList();

        sb.AppendLine($"\n## Consumidores ({grouped.Count})\n");
        if (grouped.Count == 0)
        {
            sb.AppendLine("*(Nada en el grafo la usa ni llama a sus métodos)*");
        }
        else
        {
            sb.AppendLine("| Consumidor | Proyecto | Aristas |");
            sb.AppendLine("|---|---|---|");
            foreach (var g in grouped.Take(limit))
                sb.AppendLine($"| `{g.Owner}` | {g.Project} | {g.Rels} |");
            sb.AppendLine($"\nPor proyecto: {string.Join(", ", grouped.GroupBy(g => g.Project).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"`{x.Key}` {x.Count()}"))}.");
        }
        return sb.ToString();
    }

    private async Task<string> ContractsOfProjectAsync(string? dbPath, IReadOnlyList<ProjectRow> projects, ProjectRow project, int limit, CancellationToken ct)
    {
        // Aristas de uso hacia tipos (USAGE, IMPLEMENTS, INHERITS) y llamadas a métodos, resueltas al tipo que los declara.
        var rows = await _graphDb.SelectAsync(
            "SELECT s.file_path AS src_file, t.id AS id, t.label AS label, t.name AS name, t.qualified_name AS qn, t.file_path AS file_path, " +
            $"{SafeProps("t")} AS props, e.type AS rel FROM edges e JOIN nodes s ON s.id = e.source_id " +
            "JOIN nodes t ON t.id = e.target_id AND t.label IN ('Class','Interface','Enum') " +
            "WHERE e.type IN ('USAGE','IMPLEMENTS','INHERITS') " +
            "UNION ALL " +
            "SELECT s.file_path, t.id, t.label, t.name, t.qualified_name, t.file_path, " +
            $"{SafeProps("t")}, 'CALLS' FROM edges e JOIN nodes s ON s.id = e.source_id " +
            "JOIN edges dm ON dm.target_id = e.target_id AND dm.type = 'DEFINES_METHOD' " +
            "JOIN nodes t ON t.id = dm.source_id AND t.label IN ('Class','Interface','Enum') " +
            "WHERE e.type = 'CALLS'", null, dbPath, ct);

        var used = new Dictionary<long, (TypeRow Type, string OwnerProject, SortedSet<string> Rels)>();
        foreach (var r in rows)
        {
            if (!string.Equals(ProjectOfFile(projects, r.GetValueOrDefault("src_file")?.ToString()), project.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            var type = ToTypeRow(r);
            var owner = type.Project ?? ProjectOfFile(projects, type.FilePath) ?? "-";
            if (string.Equals(owner, project.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!used.TryGetValue(type.Id, out var entry))
                used[type.Id] = entry = (type, owner, []);
            entry.Rels.Add(r.GetValueOrDefault("rel")?.ToString() ?? "");
        }

        var sb = new StringBuilder($"# Qué usa `{project.Name}` de otros proyectos\n\n");
        if (used.Count == 0)
        {
            sb.AppendLine("*(Ningún tipo de otro proyecto aparece usado desde este proyecto en el grafo)*");
            return sb.ToString();
        }

        sb.AppendLine("«Contrato» = interfaz o clase abstracta pública; «implementación» = clase concreta. Depender de implementaciones es depender de detalles del otro proyecto.\n");
        sb.AppendLine("## Resumen por proyecto\n");
        sb.AppendLine("| Proyecto usado | Contratos | Implementaciones | Infraestructura | Modelo | Sin clasificar |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var g in used.Values.GroupBy(u => u.OwnerProject).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var counts = TypeRoleOrder.Select(role => g.Count(u => u.Type.Role == role)).ToList();
            sb.AppendLine($"| `{g.Key}` | {string.Join(" | ", counts)} | {g.Count(u => u.Type.Role is null)} |");
        }

        sb.AppendLine("\n## Tipos usados\n");
        sb.AppendLine("| Proyecto | Tipo | Papel | Visibilidad | Aristas |");
        sb.AppendLine("|---|---|---|---|---|");
        var ordered = used.Values
            .OrderBy(u => u.Type.Role == "contract" ? 1 : 0)
            .ThenBy(u => u.OwnerProject, StringComparer.Ordinal).ThenBy(u => u.Type.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var u in ordered.Take(limit))
            sb.AppendLine($"| {u.OwnerProject} | `{u.Type.Name}` | {Role(u.Type.Role)} | {Visibility(u.Type.IsPublic)} | {string.Join(", ", u.Rels)} |");
        if (ordered.Count > limit)
            sb.AppendLine($"\n*({ordered.Count - limit} tipos más; sube `limit`)*");
        return sb.ToString();
    }
}

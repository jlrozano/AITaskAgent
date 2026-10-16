using CodebaseAnalysis.Core.Support;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Valores que el compilador resuelve como constantes (<c>IOperation.ConstantValue</c>, es decir <c>GetConstantValue</c>): lo que
/// una expresión regular sobre el texto no ve cuando el valor está en un <c>const</c>, en una concatenación de constantes o en
/// una cadena interpolada. Escribe en el nodo del método, del campo o del tipo que contiene la expresión:
///
///   - <c>hardcoded_secrets</c>: <c>[{kind, name, line, preview_masked}]</c>. Una credencial escrita en claro: una cadena
///     constante asignada a un identificador con nombre de credencial (<c>password</c>, <c>secret</c>, <c>token</c>…), pasada a un
///     parámetro con ese nombre, a un <c>SetPassword</c> o a un <c>Dictionary["password"]</c>, o una cadena de conexión con
///     <c>Password=</c> o <c>Pwd=</c>. <c>kind</c> es <c>identifier</c>, <c>call-argument</c> o <c>connection-string</c>. El valor
///     sale ENMASCARADO (<c>Or*** (11 caracteres)</c>) y el secreto completo no se guarda en ningún sitio. Se descartan los
///     identificadores que nombran una referencia a la credencial (<c>UserSecretsId</c>) y los marcadores de posición.
///   - <c>reflection_targets</c>: <c>string[]</c> con el nombre de tipo que se pasa a <c>Type.GetType</c>, <c>Assembly.GetType</c>,
///     <c>Assembly.CreateInstance</c> o <c>Activator.CreateInstance(assembly, tipo)</c>, con <c>*</c> por cada tramo que no es
///     constante (<c>Orders.Core.Notifications.*</c>). Sin un tramo fijo suficiente no se escribe.
///
/// Lo consume <see cref="ReflectionTargetSuppressor"/> para no dar por muertas las clases que el patrón puede nombrar, y las
/// facetas de la app (APIs, errores y código muerto).
/// </summary>
public class ConstantEnricher : IGraphEnricher
{
    public string Name  => "ConstantEnricher";
    public int    Order => 9;

    private const int MinSecretLength = 3;
    private const int MinFixedTargetChars = 6;

    private sealed record Secret(string Kind, string? Name, int Line, string Masked);

    private sealed class Findings
    {
        public List<Secret> Secrets { get; } = [];
        public SortedSet<string> Targets { get; } = new(StringComparer.Ordinal);
    }

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph = ctx.Repository;
        var project = ctx.Project;

        // Idempotencia: el parche solo suma, así que se retira lo de la pasada anterior antes de recalcular.
        await graph.ExecuteAsync($"""
            UPDATE nodes SET properties = json_remove(properties, '$.hardcoded_secrets', '$.reflection_targets')
            WHERE project = @project AND {LanguageScope.NotJava()}
              AND (json_type(properties, '$.hardcoded_secrets') IS NOT NULL OR json_type(properties, '$.reflection_targets') IS NOT NULL)
            """, new { project });

        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        var findings = new Dictionary<long, Findings>();

        foreach (var compilation in ctx.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees.Where(t => !filter.ShouldExclude(t.FilePath)))
            {
                var model = compilation.GetSemanticModel(tree);
                var text = await tree.GetTextAsync();
                foreach (var node in (await tree.GetRootAsync()).DescendantNodes())
                {
                    var found = Inspect(node, model, text).ToList();
                    if (found.Count == 0) continue;
                    if (await OwnerNodes.ForAsync(node, model, ctx) is not { } id) continue;
                    if (!findings.TryGetValue(id, out var f)) findings[id] = f = new Findings();
                    foreach (var item in found)
                    {
                        // La misma cadena en la misma línea es un solo hallazgo, la nombre el parámetro o el método.
                        if (item is Secret s && !f.Secrets.Any(x => x.Kind == s.Kind && x.Line == s.Line && x.Masked == s.Masked)) f.Secrets.Add(s);
                        else if (item is string target) f.Targets.Add(target);
                    }
                }
            }
        }

        foreach (var (id, f) in findings)
        {
            var patch = new Dictionary<string, object?>();
            if (f.Secrets.Count > 0)
            {
                patch["hardcoded_secrets"] = f.Secrets.OrderBy(s => s.Line).ThenBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)
                    .Select(s => new Dictionary<string, object?> { ["kind"] = s.Kind, ["name"] = s.Name, ["line"] = s.Line, ["preview_masked"] = s.Masked })
                    .ToList();
            }
            if (f.Targets.Count > 0)
                patch["reflection_targets"] = f.Targets.ToArray();
            if (patch.Count > 0)
                await graph.PatchNodePropertiesByIdAsync(id, patch);
        }

        progress?.Report($"Constants: {findings.Values.Sum(f => f.Secrets.Count)} hardcoded secret(s) and {findings.Values.Sum(f => f.Targets.Count)} reflection target(s) on {findings.Count} node(s).");
    }

    // ─── Reglas ──────────────────────────────────────────────────────────────────

    /// <summary>Lo que dice un nodo de sintaxis: <see cref="Secret"/> o el patrón (<see cref="string"/>) de un nombre de tipo.</summary>
    private static IEnumerable<object> Inspect(SyntaxNode node, SemanticModel model, Microsoft.CodeAnalysis.Text.SourceText text)
    {
        int LineOf(SyntaxNode n) => text.Lines.GetLinePosition(n.SpanStart).Line + 1;

        switch (node)
        {
            // string Password = "x";  const string ApiKey = "x";  campo, local o propiedad con inicializador.
            case VariableDeclaratorSyntax { Initializer.Value: { } value } declarator when MayBeConstantString(value):
                foreach (var s in Candidates("identifier", declarator.Identifier.ValueText, value, model, LineOf(value))) yield return s;
                break;
            case PropertyDeclarationSyntax { Initializer.Value: { } value } property when MayBeConstantString(value):
                foreach (var s in Candidates("identifier", property.Identifier.ValueText, value, model, LineOf(value))) yield return s;
                break;

            // password = "x";  this.Password = "x";  settings["password"] = "x";
            case AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment:
                var name = assignment.Left switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    ElementAccessExpressionSyntax { ArgumentList.Arguments: [{ Expression: { } key }] } => ConstantString(key, model),
                    _ => null
                };
                foreach (var s in Candidates("identifier", name, assignment.Right, model, LineOf(assignment.Right))) yield return s;
                break;

            // Argumentos: a un parámetro llamado password/secret…, o una cadena de conexión con contraseña.
            case ArgumentSyntax argument when MayBeConstantString(argument.Expression) && ConstantString(argument.Expression, model) is not null:
                var paramName = argument.NameColon?.Name.Identifier.ValueText ?? ParameterOf(argument, model)?.Name;
                foreach (var s in Candidates("call-argument", paramName, argument.Expression, model, LineOf(argument.Expression),
                             referenced: argument.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax)) yield return s;
                break;

            // Una cadena de conexión con contraseña escrita en cualquier sitio (rama de un ternario, return, elemento de una lista): el
            // mismo hallazgo que el de su declaración o argumento, si lo hay, cuenta una vez.
            case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal:
                if (ConnectionPassword(literal.Token.ValueText) is { } inLiteral)
                    yield return new Secret("connection-string", null, LineOf(literal), CredentialNames.Mask(inLiteral));
                break;

            // SetPassword("x"), SetEnvironmentVariable("DB_PASSWORD", "x"), Add("password", "x"), y los nombres de tipo en reflexión.
            case InvocationExpressionSyntax invocation:
                if (model.GetOperation(invocation) is IInvocationOperation call)
                {
                    foreach (var s in InvocationCandidates(call, model, LineOf(invocation))) yield return s;
                    if (ReflectionTarget(call) is { } target) yield return target;
                }
                break;
        }
    }

    /// <summary>Una cadena constante que es una credencial en claro por el nombre al que se asigna o por ser una cadena de conexión con contraseña.</summary>
    private static IEnumerable<Secret> Candidates(string kind, string? name, ExpressionSyntax value, SemanticModel model, int line, bool referenced = false)
    {
        // Una constante con nombre de credencial ya se avisa en su declaración: no otra vez donde se usa.
        if (referenced && CredentialNames.NamesACredential(SimpleName(value))) yield break;
        if (ConstantString(value, model) is not { Length: >= MinSecretLength } text) yield break;

        if (CredentialNames.NamesACredential(name) && !CredentialNames.NamesAReference(name!) && !CredentialNames.IsPlaceholder(text))
            yield return new Secret(kind, name, line, CredentialNames.Mask(text));

        // Una referencia a otra constante tampoco repite la cadena de conexión de su declaración.
        if (!referenced && ConnectionPassword(text) is { } password)
            yield return new Secret("connection-string", name, line, CredentialNames.Mask(password));
    }

    private static IEnumerable<Secret> InvocationCandidates(IInvocationOperation call, SemanticModel model, int line)
    {
        var method = call.TargetMethod.Name;
        var args = call.Arguments;

        // SetPassword("x"): un método Set* con nombre de credencial y un argumento constante.
        if (method.StartsWith("Set", StringComparison.Ordinal) && CredentialNames.NamesACredential(method) && args.Length == 1
            && args[0].Value.ConstantValue is { HasValue: true, Value: string single } && single.Length >= MinSecretLength && !CredentialNames.IsPlaceholder(single))
            yield return new Secret("call-argument", method, line, CredentialNames.Mask(single));

        // Pares clave/valor: Add("password", "x"), SetEnvironmentVariable("DB_PASSWORD", "x"), AppSettings.Set("token", "x").
        if (args.Length == 2 && method is "Add" or "TryAdd" or "Set" or "SetValue" or "SetEnvironmentVariable" or "SetProperty" or "Insert")
        {
            if (args[0].Value.ConstantValue is { HasValue: true, Value: string key } && CredentialNames.NamesACredential(key) && !CredentialNames.NamesAReference(key)
                && args[1].Value.ConstantValue is { HasValue: true, Value: string value } && value.Length >= MinSecretLength && !CredentialNames.IsPlaceholder(value))
                yield return new Secret("call-argument", key, line, CredentialNames.Mask(value));
        }
    }

    // ─── Reflexión ───────────────────────────────────────────────────────────────

    /// <summary>
    /// El patrón del nombre de tipo en <c>Type.GetType(nombre)</c>, <c>Assembly.GetType</c>/<c>CreateInstance</c> o
    /// <c>Activator.CreateInstance(ensamblado, tipo)</c>; null si no es una de ellas o el nombre no deja un tramo fijo útil.
    /// </summary>
    private static string? ReflectionTarget(IInvocationOperation call)
    {
        var method = call.TargetMethod;
        var owner = method.ContainingType.ToDisplayString();
        IArgumentOperation? nameArgument = null;
        if (method.Name == "GetType" && owner is "System.Type" or "System.Reflection.Assembly")
            nameArgument = call.Arguments.FirstOrDefault(a => a.Parameter?.Type.SpecialType == SpecialType.System_String);
        else if (method.Name == "CreateInstance" && owner == "System.Reflection.Assembly")
            nameArgument = call.Arguments.FirstOrDefault(a => a.Parameter?.Type.SpecialType == SpecialType.System_String);
        else if (method.Name == "CreateInstance" && owner == "System.Activator" && method.Parameters.Length >= 2
                 && method.Parameters[0].Type.SpecialType == SpecialType.System_String && method.Parameters[1].Type.SpecialType == SpecialType.System_String)
            nameArgument = call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 1);

        if (nameArgument is null) return null;
        var pattern = Pattern(nameArgument.Value);
        // "Tipo, Ensamblado": solo cuenta el nombre del tipo.
        var comma = pattern.IndexOf(',');
        if (comma >= 0) pattern = pattern[..comma];
        pattern = CollapseStars(pattern.Trim().Replace('+', '.'));
        return UsefulTarget(pattern) ? pattern : null;
    }

    private static bool UsefulTarget(string pattern)
    {
        if (pattern.Length == 0 || pattern == "*") return false;
        if (!pattern.Contains('*')) return true;
        return pattern.Replace("*", "").Length >= MinFixedTargetChars && pattern.IndexOf('.') > 0;
    }

    private static string CollapseStars(string text)
    {
        while (text.Contains("**", StringComparison.Ordinal)) text = text.Replace("**", "*", StringComparison.Ordinal);
        return text;
    }

    /// <summary>El texto de la expresión con <c>*</c> por cada parte que no es constante.</summary>
    private static string Pattern(IOperation operation)
    {
        if (operation.ConstantValue is { HasValue: true } constant)
            return Convert.ToString(constant.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "*";
        return operation switch
        {
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } binary => Pattern(binary.LeftOperand) + Pattern(binary.RightOperand),
            IInterpolatedStringOperation interpolated => string.Concat(interpolated.Parts.Select(part => part switch
            {
                IInterpolatedStringTextOperation literal => Pattern(literal.Text),
                IInterpolationOperation interpolation => Pattern(interpolation.Expression),
                _ => "*"
            })),
            IConversionOperation conversion => Pattern(conversion.Operand),
            IParenthesizedOperation parenthesized => Pattern(parenthesized.Operand),
            _ => "*"
        };
    }

    // ─── Utilidades ──────────────────────────────────────────────────────────────

    /// <summary>Expresiones que pueden ser una cadena constante: así no se pide la operación de cada llamada o lambda del repositorio.</summary>
    private static bool MayBeConstantString(ExpressionSyntax expression) =>
        expression.Kind() is SyntaxKind.StringLiteralExpression or SyntaxKind.InterpolatedStringExpression or SyntaxKind.AddExpression
            or SyntaxKind.IdentifierName or SyntaxKind.SimpleMemberAccessExpression or SyntaxKind.ParenthesizedExpression;

    private static string? ConstantString(ExpressionSyntax expression, SemanticModel model) =>
        MayBeConstantString(expression) && model.GetOperation(expression)?.ConstantValue is { HasValue: true, Value: string text } ? text : null;

    private static IParameterSymbol? ParameterOf(ArgumentSyntax argument, SemanticModel model)
    {
        var owner = argument.Parent?.Parent;
        if (owner is null) return null;
        var arguments = model.GetOperation(owner) switch
        {
            IInvocationOperation invocation => invocation.Arguments,
            IObjectCreationOperation creation => creation.Arguments,
            _ => default
        };
        return arguments.IsDefault ? null : arguments.FirstOrDefault(a => a.Syntax == argument || a.Value.Syntax == argument.Expression)?.Parameter;
    }

    private static string? SimpleName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };

    /// <summary>La contraseña de <c>...;Pwd=x;...</c> o <c>Password=x</c>; null si no hay o es un marcador de posición.</summary>
    private static string? ConnectionPassword(string text)
    {
        foreach (var key in new[] { "password", "pwd" })
        {
            for (var at = text.IndexOf(key, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(key, at + key.Length, StringComparison.OrdinalIgnoreCase))
            {
                if (at > 0 && (char.IsLetterOrDigit(text[at - 1]) || text[at - 1] == '_')) continue;
                var i = at + key.Length;
                while (i < text.Length && text[i] == ' ') i++;
                if (i >= text.Length || text[i] != '=') continue;
                i++;
                while (i < text.Length && text[i] == ' ') i++;
                var end = i;
                while (end < text.Length && text[end] is not (';' or '"' or '\'' or ' ' or '&' or '+')) end++;
                var value = text[i..end];
                if (value.Length >= MinSecretLength && !CredentialNames.IsPlaceholder(value)) return value;
            }
        }
        return null;
    }
}

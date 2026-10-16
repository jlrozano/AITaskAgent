namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Las reglas de nombres de credenciales que comparten el escáner de disco (<see cref="HardcodedSecretScanner"/>), el escritor de
/// los parches del analizador Java y el enriquecedor Roslyn (que enlaza este fichero, por eso no depende de nada de la app):
/// qué identificadores nombran una referencia a la credencial y no la credencial, qué valores no son un secreto y cómo se
/// enmascara el valor para que ningún informe ni el grafo guarde el secreto completo.
/// </summary>
public static class CredentialNames
{
    // Último segmento de un identificador que nombra una referencia a la credencial y no la credencial:
    // UserSecretsId, PasswordFileName, token_url, SecretProvider, passwordLength... Su valor es un GUID, una
    // ruta, un nombre o una cifra. Se compara el último segmento (PascalCase, camelCase o snake_case), así que
    // ApiKey, ClientSecret, access_token o authTokenValue siguen contando.
    private static readonly HashSet<string> ReferenceSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "Ids", "Name", "Names", "Path", "Paths", "File", "Files", "Dir", "Directory", "Folder",
        "Url", "Uri", "Endpoint", "Provider", "Type", "Kind", "Mode", "Format", "Length", "Count", "Size",
        "Policy", "Label", "Field", "Column", "Header", "Param", "Parameter", "Variable", "Var", "Env",
        "Prompt", "Hint", "Placeholder", "Pattern", "Regex", "Timeout", "Expiry", "Expiration", "Lifetime", "Version"
    };


    /// <summary>Un identificador de credencial: contiene <c>password</c>, <c>passwd</c>, <c>pwd</c>, <c>secret</c>, <c>apikey</c>, <c>token</c> o <c>credential</c>.</summary>
    public static bool NamesACredential(string? identifier) =>
        !string.IsNullOrEmpty(identifier) && CredentialWords.Any(w => identifier.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] CredentialWords = ["password", "passwd", "pwd", "secret", "apikey", "api_key", "token", "credential"];

    /// <summary>True si el identificador acaba en un segmento de <see cref="ReferenceSuffixes"/>.</summary>
    public static bool NamesAReference(string identifier)
    {
        var trimmed = identifier.Trim('_');
        var underscore = trimmed.LastIndexOf('_');
        string last;
        if (underscore >= 0)
        {
            last = trimmed[(underscore + 1)..];
        }
        else
        {
            // Último segmento en PascalCase o camelCase; un identificador todo en mayúsculas es un solo segmento.
            var start = trimmed.Length - 1;
            while (start > 0)
            {
                var c = trimmed[start];
                var previous = trimmed[start - 1];
                // "userSecretsId" -> "Id"; con siglas, "SecretURLPath" -> "Path".
                if (char.IsUpper(c) && (char.IsLower(previous) || char.IsDigit(previous)))
                    break;
                if (char.IsUpper(c) && char.IsUpper(previous) && start + 1 < trimmed.Length && char.IsLower(trimmed[start + 1]))
                    break;
                start--;
            }
            last = trimmed[start..];
        }

        return last.Length < trimmed.Length && ReferenceSuffixes.Contains(last);
    }

    /// <summary>Referencias a variables, plantillas o valores ya ocultos: no son un secreto en claro.</summary>
    public static bool IsPlaceholder(string value) =>
        value.StartsWith("${", StringComparison.Ordinal)
        || value.StartsWith('{') || value.StartsWith('<') || value.StartsWith('%')
        || value.All(c => c == '*' || c == 'x' || c == 'X');

    /// <summary>Los dos primeros caracteres y la longitud: <c>Or*** (11 caracteres)</c>.</summary>
    public static string Mask(string value) => $"{value[..Math.Min(2, value.Length)]}*** ({value.Length} caracteres)";
}

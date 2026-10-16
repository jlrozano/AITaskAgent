using AITaskAgent.FileTools;
using AITaskAgent.FileTools.Tools;
using AITaskAgent.LLM.Tools.Implementation;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Notes;
using CodebaseAnalysis.Core.Tools;
using OfficeTools.Extensions;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>
/// Registra en <see cref="AvailableTools"/> las tools del grafo, las de Office y las de FileTools que usan
/// los auditores. Tiene que ir antes de construir el pipeline: <c>TemplateLlmStep</c> resuelve sus tools
/// por nombre en el constructor, y registradas después los auditores se quedaban sin ninguna.
/// Registra también las del chat: informes (<c>list_reports</c>, <c>read_report</c>) y búsqueda en el repositorio
/// (<c>list_dir</c>, <c>find_by_name</c>, <c>grep_search</c>, de solo lectura).
/// </summary>
public static class AnalysisToolRegistration
{
    public static void Register(AnalysisPipelineInput input)
    {
        AvailableTools.Instance.Register(new GraphMapTool(input.DatabasePath));
        AvailableTools.Instance.Register(new GraphHealthTool(input.DatabasePath));
        AvailableTools.Instance.Register(new GraphDomainTool(input.DatabasePath));
        AvailableTools.Instance.Register(new GraphFlowsTool(input.DatabasePath));
        AvailableTools.Instance.Register(new GraphSearchTool(input.DatabasePath));
        // Vecinos de un nodo (llamadas, jerarquía, aristas Struts/JSP), con la procedencia de cada arista. La usan el chat y
        // los auditores (AnalysisPipelineFactory.AuditorToolNames).
        AvailableTools.Instance.Register(new GraphNeighborsTool(input.DatabasePath));
        AvailableTools.Instance.RegisterOfficeTools(input.DatabasePath, input.RepositoryPath);

        // El sandbox de FileTools usa por defecto <directorio actual>/workspace: view_file rechazaba
        // cualquier fichero del repositorio analizado. Las tools registradas aquí son de solo lectura.
        FileToolsConfiguration.RootDirectory = input.RepositoryPath;
        // Los ficheros que no son UTF-8 válido se leen con la codificación del repositorio (JavaAnalyzer:DefaultEncoding o
        // pom.xml) o, si no declara ninguna, con Latin-1: las tildes de un repositorio ISO-8859-1 salían como U+FFFD (H-8).
        FileToolsConfiguration.DefaultEncoding = SourceEncoding.ResolveReadFallback(input.RepositoryPath, input.SourceDefaultEncoding);
        AvailableTools.Instance.Register(new ViewFileTool());
        AvailableTools.Instance.Register(new ViewFileOutlineTool());
        // view_code_item fuera de C#: el rango de líneas del nodo del grafo (en Java, el de javac que escribe el analizador) y,
        // sin grafo o sin nodo, el localizador por llaves para Java. Los .cs siguen con Roslyn.
        AvailableTools.Instance.Register(new ViewCodeItemTool(
            [new GraphCodeItemLocator(input.DatabasePath, input.RepositoryPath, fallback: new JavaCodeItemLocator())]));

        // grep_search no entra en la carpeta de la BD (copia, JSONL y logs del análisis) ni en la de informes: una búsqueda de
        // un sufijo en el repositorio encontraba el informe anterior, que no es código.
        FileToolsConfiguration.SearchExcludedDirectories =
        [
            .. new[] { Path.GetDirectoryName(input.DatabasePath), input.OutputReportsPath }
                .Where(d => !string.IsNullOrWhiteSpace(d) && !IsRepositoryRoot(input.RepositoryPath, d!))
                .Select(d => d!)
        ];

        // Búsqueda en el repositorio para el chat y los auditores (grep_search): localizar ficheros y líneas antes de
        // abrirlos, en vez de adivinar rutas.
        AvailableTools.Instance.Register(new ListDirTool());
        AvailableTools.Instance.Register(new FindByNameTool());
        AvailableTools.Instance.Register(new GrepSearchTool());

        // Tools del chat sobre los informes. Leen la carpeta de informes del contexto; ningún auditor las usa.
        AvailableTools.Instance.Register(new ListReportsTool());
        AvailableTools.Instance.Register(new ReadReportTool());

        // V2: escritura de informes (solo con el ChatTurnScope de un turno de chat).
        AvailableTools.Instance.Register(new CreateReportTool());
        AvailableTools.Instance.Register(new UpdateReportTool());

        // V2-C/D: documentos aportados y búsqueda semántica. read_document la usan también los auditores, que no
        // tienen KnowledgePaths en el contexto: se registran con las del repositorio.
        var knowledge = new Knowledge.KnowledgePaths(input.RepositoryPath, input.OutputReportsPath);
        AvailableTools.Instance.Register(new ListDocumentsTool(knowledge));
        AvailableTools.Instance.Register(new ReadDocumentTool(knowledge));
        AvailableTools.Instance.Register(new DescribeImageTool(knowledge));
        AvailableTools.Instance.Register(new SemanticSearchTool());

        // Protocolo de notas: las tools de lectura de los auditores, envueltas para marcar cada resultado con [Rn] cuando el paso
        // que las llama tiene sesión de notas. Sin sesión (el chat, el verificador) devuelven lo mismo que sin envolver.
        foreach (var name in AnalysisPipelineFactory.AuditorToolNames)
        {
            if (AvailableTools.Instance.GetTool(name) is { } tool)
                AvailableTools.Instance.Register(NoteProtocolTool.Wrap(tool));
        }
    }

    /// <summary>Excluir la raíz dejaría la búsqueda vacía: una BD o unos informes en la raíz no se excluyen.</summary>
    private static bool IsRepositoryRoot(string repositoryPath, string directory) =>
        !string.IsNullOrWhiteSpace(repositoryPath)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase);
}

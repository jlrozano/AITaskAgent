using AITaskAgent.FileTools;
using AITaskAgent.FileTools.Tools;
using AITaskAgent.LLM.Tools.Implementation;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Models;
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
        AvailableTools.Instance.Register(new ViewCodeItemTool());

        // Búsqueda en el repositorio para el chat: localizar ficheros y líneas antes de abrirlos, en vez de adivinar rutas.
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
    }
}

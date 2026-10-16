namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// Detecta la infraestructura compartida del repositorio (experimento E2 de la medición): los ficheros de los que dependen muchas comunidades
/// distintas y que no son capacidad de nadie (utilidades de dinero y de fechas, configuración de base de datos, el marco de acciones, el
/// registro de errores). Una capacidad de negocio que la absorbe por casualidad (<c>Money</c> cae donde la usa más gente) se parece a
/// aquello de lo que solo depende; agrupada aparte, cada capacidad queda con lo suyo y el informe de arquitectura tiene un sitio donde mirarla.
/// </summary>
/// <remarks>
/// <para>Un fichero es compartido si cumple las cuatro condiciones, todas calculadas sobre las comunidades ya formadas y sin ningún nombre:</para>
/// <list type="number">
/// <item><b>Ancho:</b> dependen de él tipos de al menos <see cref="PreparationOptions.SharedMinConsumers"/> comunidades distintas (la suya
/// incluida) y de al menos la fracción <see cref="PreparationOptions.SharedMinConsumerShare"/> de todas las comunidades: en un repositorio
/// con cuarenta capacidades, depender de tres no hace de nadie infraestructura.</item>
/// <item><b>Base:</b> depende de pocas comunidades ajenas, como mucho <see cref="MaxFanOutRatio"/> de las que dependen de él. Una entidad que usa
/// todo el mundo pero que a su vez referencia a muchas capacidades es dominio, no base.</item>
/// <item><b>Sin entradas:</b> ningún punto de entrada del inventario está en el fichero (una acción, un <c>main</c> o un proceso planificado
/// es una capacidad hacia fuera, no un servicio hacia dentro).</item>
/// <item><b>Sin estados:</b> ningún tipo suyo es un enumerado (los estados y los tipos de algo pertenecen a lo que modelan).</item>
/// </list>
/// <para>Un único fichero no forma un grupo (<see cref="MinFiles"/>): se queda donde está.</para>
/// </remarks>
public static class SharedInfrastructure
{
    /// <summary>Ficheros que, como mínimo, tiene que sumar la infraestructura compartida para ser un subsistema.</summary>
    public const int MinFiles = 2;

    /// <summary>
    /// Máximo de comunidades ajenas de las que puede depender un fichero compartido, como fracción de las ajenas que dependen de él: una base
    /// recibe mucho más de lo que referencia.
    /// </summary>
    public const double MaxFanOutRatio = 0.25;

    /// <summary>Umbral efectivo de comunidades consumidoras para <paramref name="communities"/> comunidades; 0 si el subsistema compartido está desactivado.</summary>
    public static int Threshold(int communities, PreparationOptions options)
    {
        if (options.SharedMinConsumers <= 0)
            return 0;
        return Math.Max(options.SharedMinConsumers, (int)Math.Ceiling(options.SharedMinConsumerShare * communities - 1e-9));
    }

    /// <summary>
    /// Los ficheros (posiciones de <paramref name="clusterOfFile"/>) que son infraestructura compartida, ordenados; vacío si no llegan a
    /// <see cref="MinFiles"/> o está desactivado.
    /// </summary>
    /// <param name="graph">El grafo de tipos: una arista va del tipo que usa al usado.</param>
    /// <param name="fileOfNode">Fichero de cada nodo del grafo (-1 si no es un tipo de producción).</param>
    /// <param name="membersOfFile">Nodos de cada fichero.</param>
    /// <param name="clusterOfFile">Comunidad de cada fichero (-1 si no tiene).</param>
    /// <param name="communities">Cuántas comunidades hay (las que cuentan para la fracción del umbral).</param>
    /// <param name="filesWithEntries">Ficheros con algún punto de entrada del inventario.</param>
    /// <param name="options">Aporta el umbral de comunidades consumidoras.</param>
    public static IReadOnlyList<int> Detect(
        TypeGraph graph, int[] fileOfNode, List<int>[] membersOfFile, int[] clusterOfFile, int communities,
        IReadOnlySet<int> filesWithEntries, PreparationOptions options)
    {
        var threshold = Threshold(communities, options);
        if (threshold <= 0 || communities < 2)
            return [];

        var files = clusterOfFile.Length;
        var consumers = new HashSet<int>[files];
        var outward = new HashSet<int>[files];
        for (var i = 0; i < files; i++)
        {
            consumers[i] = [];
            outward[i] = [];
        }

        foreach (var edge in graph.Edges)
        {
            var from = fileOfNode[edge.Source];
            var to = fileOfNode[edge.Target];
            if (from < 0 || to < 0 || from == to || clusterOfFile[from] < 0 || clusterOfFile[to] < 0)
                continue;
            consumers[to].Add(clusterOfFile[from]);
            if (clusterOfFile[from] != clusterOfFile[to])
                outward[from].Add(clusterOfFile[to]);
        }

        var shared = new List<int>();
        for (var file = 0; file < files; file++)
        {
            if (clusterOfFile[file] < 0 || membersOfFile[file].Count == 0 || filesWithEntries.Contains(file))
                continue;
            if (membersOfFile[file].Any(n => graph.Nodes[n].Label == "Enum"))
                continue;
            if (consumers[file].Count < threshold)
                continue;
            var foreign = consumers[file].Count - (consumers[file].Contains(clusterOfFile[file]) ? 1 : 0);
            if (outward[file].Count > MaxFanOutRatio * foreign)
                continue;
            shared.Add(file);
        }

        return shared.Count >= MinFiles ? shared : [];
    }
}

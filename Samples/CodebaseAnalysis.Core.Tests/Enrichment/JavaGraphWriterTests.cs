using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// Grafo sintético que imita lo que deja cbm al indexar <see cref="JavaTestRepo"/> (QN por ruta, sobrecargas fundidas en un
/// nodo con la línea de la última, <c>Function</c> de los métodos de interfaz, <c>CALLS</c> decididas por nombre, el código
/// de test indexado, un nodo con <c>properties</c> que no es JSON válido y los campos como <c>Field</c> y <c>Variable</c>),
/// y el resultado del analizador Java para ese mismo repositorio escrito según el contrato JSONL. Ese resultado sintético no
/// incluye el código añadido para las referencias (<c>Filtro</c>, <c>Servicio.buscar</c>, <c>Servicio.contarUsos</c> y los
/// campos de <c>Servicio</c>): tiene sus nodos de cbm y solo lo recorre el analizador real, en los tests con JDK.
/// </summary>
internal static class JavaGraphFixture
{
    public const string Project = "C-tmp-acme";
    public const string IndexedAt = "2026-09-28T10:00:00Z";

    public static string Prefix(string path) => $"{Project}.{path[..path.LastIndexOf('.')].Replace('/', '.')}";

    public static string Qn(string path, string rest) => $"{Prefix(path)}.{rest}";

    public static readonly string DaoConsultar = Qn(JavaTestRepo.DaoPath, "Dao.consultar");
    public static readonly string DaoFunction = Qn(JavaTestRepo.DaoPath, "consultar");
    public static readonly string DaoImplConsultar = Qn(JavaTestRepo.DaoImplPath, "DaoImpl.consultar");
    public static readonly string Ejecutar = Qn(JavaTestRepo.ServicioPath, "Servicio.ejecutar");
    public static readonly string BeanQn = Qn(JavaTestRepo.BeanPath, "Bean");
    public static readonly string BeanId = Qn(JavaTestRepo.BeanPath, "Bean.id");
    public static readonly string GetId = Qn(JavaTestRepo.BeanPath, "Bean.getId");
    public static readonly string FiltroQn = Qn(JavaTestRepo.FiltroPath, "Filtro");
    public static readonly string ServicioQn = Qn(JavaTestRepo.ServicioPath, "Servicio");
    public static readonly string Buscar = Qn(JavaTestRepo.ServicioPath, "Servicio.buscar");
    public static readonly string ContarUsos = Qn(JavaTestRepo.ServicioPath, "Servicio.contarUsos");

    /// <summary>Campos nuevos de <c>Servicio</c> con el QN de cbm, que lleva el inicializador en el nombre.</summary>
    public static readonly string LimiteField = Qn(JavaTestRepo.ServicioPath, "Servicio.LIMITE = 10");
    public static readonly string PendienteField = Qn(JavaTestRepo.ServicioPath, "Servicio.pendiente");
    public static readonly string UsosField = Qn(JavaTestRepo.ServicioPath, "Servicio.usos");

    private static int Line(string path, string fragment) => JavaTestRepo.LineOf(path, fragment);

    public static async Task<CbmTestGraph> CreateGraphAsync(string databasePath, bool withInterfaceMethodNodes = true)
    {
        var g = await CbmTestGraph.CreateAsync(databasePath, Project, IndexedAt);
        var folder = await g.NodeAsync("Folder", "test", $"{Project}.test", "test");

        async Task<long> FileAsync(string path)
        {
            var file = await g.NodeAsync("File", Path.GetFileName(path), $"{Prefix(path)}.__file__", path, 0, 0, "{\"extension\":\".java\"}");
            await g.NodeAsync("Module", path, Prefix(path), path, 1, JavaTestRepo.LineCount(path));
            return file;
        }

        // Bean (UTF-8): cbm guarda el Javadoc en bruto.
        await FileAsync(JavaTestRepo.BeanPath);
        var bean = await g.NodeAsync("Class", "Bean", Qn(JavaTestRepo.BeanPath, "Bean"), JavaTestRepo.BeanPath, 6, 12,
            "{\"is_test\":false,\"docstring\":\"/**\\r\\n * Año de la acción.\\r\\n */\"}");
        var getId = await g.NodeAsync("Method", "getId", Qn(JavaTestRepo.BeanPath, "Bean.getId"), JavaTestRepo.BeanPath, 9, 11,
            "{\"signature\":\"()\",\"return_type\":\"long\",\"parent_class\":\"" + Qn(JavaTestRepo.BeanPath, "Bean") + "\"}");
        await g.EdgeAsync(bean, getId, "DEFINES_METHOD");

        // Filtro: bean que solo se usa como parámetro, con su constructor (cbm lo nombra como la clase).
        await FileAsync(JavaTestRepo.FiltroPath);
        var filtro = await g.NodeAsync("Class", "Filtro", FiltroQn, JavaTestRepo.FiltroPath, 3, 6);
        var filtroCtor = await g.NodeAsync("Method", "Filtro", Qn(JavaTestRepo.FiltroPath, "Filtro.Filtro"), JavaTestRepo.FiltroPath, 4, 5);
        await g.EdgeAsync(filtro, filtroCtor, "DEFINES_METHOD");
        // cbm guarda cada campo dos veces: un Field en <QN del tipo>.<name>, con parent_class (y con el inicializador en el
        // name, si lo tiene: «nombreConexion = null»), y un Variable en <prefijo>.<nombre>.
        await g.NodeAsync("Field", "id", BeanId, JavaTestRepo.BeanPath, 7, 7,
            "{\"return_type\":\"long\",\"parent_class\":\"" + BeanQn + "\"}");
        await g.NodeAsync("Variable", "id", Qn(JavaTestRepo.BeanPath, "id"), JavaTestRepo.BeanPath, 7, 7);

        // Dao: las dos sobrecargas fundidas en un Method con la línea de la última, y el Function de interfaz.
        await FileAsync(JavaTestRepo.DaoPath);
        var dao = await g.NodeAsync("Interface", "Dao", Qn(JavaTestRepo.DaoPath, "Dao"), JavaTestRepo.DaoPath, 5, 8);
        if (withInterfaceMethodNodes)
        {
            var daoConsultar = await g.NodeAsync("Method", "consultar", DaoConsultar, JavaTestRepo.DaoPath, 7, 7,
                "{\"signature\":\"(Hashtable c)\",\"param_types\":[\"Hashtable\"]}");
            await g.EdgeAsync(dao, daoConsultar, "DEFINES_METHOD");
        }
        await g.NodeAsync("Function", "consultar", DaoFunction, JavaTestRepo.DaoPath, 7, 7, "{\"signature\":\"(Hashtable c)\"}");

        await FileAsync(JavaTestRepo.DaoImplPath);
        var daoImpl = await g.NodeAsync("Class", "DaoImpl", Qn(JavaTestRepo.DaoImplPath, "DaoImpl"), JavaTestRepo.DaoImplPath, 5, 17,
            "{\"base_classes\":[\"implements Dao\"]}");
        var daoImplConsultar = await g.NodeAsync("Method", "consultar", DaoImplConsultar, JavaTestRepo.DaoImplPath, 10, 12);
        var cache = await g.NodeAsync("Class", "Cache", Qn(JavaTestRepo.DaoImplPath, "DaoImpl.Cache"), JavaTestRepo.DaoImplPath, 14, 16);
        var put = await g.NodeAsync("Method", "put", Qn(JavaTestRepo.DaoImplPath, "DaoImpl.Cache.put"), JavaTestRepo.DaoImplPath, 15, 15);
        await g.EdgeAsync(daoImpl, daoImplConsultar, "DEFINES_METHOD");
        await g.EdgeAsync(cache, put, "DEFINES_METHOD");

        await FileAsync(JavaTestRepo.BasePath);
        var baseType = await g.NodeAsync("Class", "Base", Qn(JavaTestRepo.BasePath, "Base"), JavaTestRepo.BasePath, 3, 5);
        var log = await g.NodeAsync("Method", "log", Qn(JavaTestRepo.BasePath, "Base.log"), JavaTestRepo.BasePath, 4, 4);
        await g.EdgeAsync(baseType, log, "DEFINES_METHOD");

        // Servicio (ISO-8859-1): properties con un salto de línea sin escapar, como los 60 nodos del repo objetivo.
        var servicioFile = await FileAsync(JavaTestRepo.ServicioPath);
        var servicioPath = JavaTestRepo.ServicioPath;
        var servicio = await g.NodeAsync("Class", "Servicio", ServicioQn, servicioPath, 6, JavaTestRepo.LineCount(servicioPath),
            "{\"base_classes\":[\"extends\nBase\"]}");
        var ejecutar = await g.NodeAsync("Method", "ejecutar", Ejecutar, JavaTestRepo.ServicioPath, 10, 20,
            "{\"docstring\":\"/**\\n * A\\ufffdade la acci\\ufffdn.\\n */\"}");
        var web = await g.NodeAsync("Method", "web", Qn(JavaTestRepo.ServicioPath, "Servicio.web"), JavaTestRepo.ServicioPath, 22, 24);
        await g.EdgeAsync(servicioFile, servicio, "DEFINES");
        await g.EdgeAsync(servicio, ejecutar, "DEFINES_METHOD");
        await g.EdgeAsync(servicio, web, "DEFINES_METHOD");
        foreach (var (qn, name, fragment, endFragment) in new[]
                 {
                     (Buscar, "buscar", "public com.acme.Bean buscar", "return (com.acme.Bean) o;"),
                     (ContarUsos, "contarUsos", "public int contarUsos", "return usos;")
                 })
        {
            var method = await g.NodeAsync("Method", name, qn, servicioPath, Line(servicioPath, fragment), Line(servicioPath, endFragment) + 1);
            await g.EdgeAsync(servicio, method, "DEFINES_METHOD");
        }
        var servicioParent = "{\"parent_class\":\"" + ServicioQn + "\"}";
        foreach (var (qn, name, fragment) in new[]
                 {
                     (LimiteField, "LIMITE = 10", "LIMITE = 10;"), (PendienteField, "pendiente", "String pendiente;"), (UsosField, "usos", "int usos;")
                 })
            await g.NodeAsync("Field", name, qn, servicioPath, Line(servicioPath, fragment), Line(servicioPath, fragment), servicioParent);

        await FileAsync(JavaTestRepo.ConsultarActionPath);
        var action = await g.NodeAsync("Class", "ConsultarAction", Qn(JavaTestRepo.ConsultarActionPath, "ConsultarAction"), JavaTestRepo.ConsultarActionPath, 3, 7);
        var execute = await g.NodeAsync("Method", "execute", Qn(JavaTestRepo.ConsultarActionPath, "ConsultarAction.execute"), JavaTestRepo.ConsultarActionPath, 4, 6);
        await g.EdgeAsync(action, execute, "DEFINES_METHOD");

        // Código de test indexado por cbm, con un vector.
        var testFile = await FileAsync(JavaTestRepo.ServicioTestPath);
        var testClass = await g.NodeAsync("Class", "ServicioTest", Qn(JavaTestRepo.ServicioTestPath, "ServicioTest"), JavaTestRepo.ServicioTestPath, 3, 5);
        var probar = await g.NodeAsync("Method", "probar", Qn(JavaTestRepo.ServicioTestPath, "ServicioTest.probar"), JavaTestRepo.ServicioTestPath, 4, 4);
        await g.EdgeAsync(folder, testFile, "CONTAINS_FILE");
        await g.EdgeAsync(testClass, probar, "DEFINES_METHOD");
        await g.EdgeAsync(probar, ejecutar, "TESTS");
        await g.VectorAsync(probar);

        // Aristas heurísticas de cbm: CALLS por nombre (una equivocada, otra a la implementación en lugar de la interfaz) e INHERITS.
        await g.EdgeAsync(ejecutar, getId, "CALLS", "{\"callee\":\"getId\",\"confidence\":0.33,\"strategy\":\"suffix_match\"}");
        await g.EdgeAsync(ejecutar, daoImplConsultar, "CALLS", "{\"callee\":\"d.consultar\",\"strategy\":\"unique_name\"}");
        await g.EdgeAsync(execute, ejecutar, "CALLS", "{\"callee\":\"toString\",\"strategy\":\"suffix_match\"}");
        await g.EdgeAsync(servicio, baseType, "INHERITS");
        await g.EdgeAsync(ejecutar, bean, "USAGE");
        return g;
    }

    internal static string J(object record) => JsonSerializer.Serialize(record);

    internal static object Type(string path, string[] chain, string fqn, string kind, int line, int end, object? super = null,
        object[]? interfaces = null, bool @abstract = false, string? doc = null) => new
    {
        k = "type", path, chain, fqn, kind, line, end, access = "public", @abstract, annotations = Array.Empty<string>(), doc,
        super, interfaces = interfaces ?? []
    };

    internal static object Method(string path, string[] chain, string owner, string name, int line, int end, string[] @params,
        string[] paramSimple, string[] paramNames, string? ret, string? retSimple, bool @abstract = false, string? doc = null,
        int cyclomatic = 1, string access = "public", bool ctor = false) => new
    {
        k = "method", path, chain, ownerFqn = owner, name, ctor, line, end,
        key = $"{owner}#{name}({string.Join(',', @params)})", @params, paramSimple, paramNames, ret, retSimple, access,
        @static = false, @abstract, annotations = Array.Empty<string>(), doc, cyclomatic
    };

    internal static object Call(string path, int line, string? from, string fromType, string? to, string origin, string kind = "call",
        string? reason = null) => new
    {
        k = "call", path, line, from, fromType, to, origin, kind, reason
    };

    internal static object Ref(string fqn, string origin) => new { fqn, origin };

    internal static object Field(string path, string[] chain, string owner, string name, int line, string? typeFqn, string access = "private",
        bool @static = false, bool final = false, bool enumConstant = false, string? doc = null) => new
    {
        k = "field", path, chain, ownerFqn = owner, name, line, end = line, typeFqn, access, @static, final, enumConstant, doc
    };

    /// <summary>Un registro <c>ref</c> de tipo; <c>from</c> y <c>fromField</c> a null hacen del tipo el origen.</summary>
    internal static object TypeRef(string path, string? from, string fromType, string to, string[] contexts, int[] lines, int? count = null,
        string? fromField = null) => new
    {
        k = "ref", path, from, fromType, fromField, to, kind = "type", access = (string?)null, contexts, origin = "project",
        count = count ?? lines.Length, lines
    };

    /// <summary>Un registro <c>ref</c> de campo, con su acceso (<c>read</c>, <c>write</c> o <c>readwrite</c>).</summary>
    internal static object FieldRef(string path, string? from, string fromType, string to, string access, int[] lines, int? count = null,
        string? fromField = null) => new
    {
        k = "ref", path, from, fromType, fromField, to, kind = "field", access, contexts = Array.Empty<string>(), origin = "project",
        count = count ?? lines.Length, lines
    };

    /// <summary>
    /// Lo que el analizador Java emite para <see cref="JavaTestRepo"/>, en el orden del contrato. Con
    /// <paramref name="withReferences"/>, el resultado es de schema 2: el campo de <c>Bean</c> y las referencias a tipos y
    /// campos del proyecto.
    /// </summary>
    public static List<string> ResultLines(bool withReferences = false)
    {
        const string ht = "java.util.Hashtable";
        var ejecutarKey = "com.acme.negocio.Servicio#ejecutar()";
        var servicio = JavaTestRepo.ServicioPath;
        var lines = new List<string>();
        foreach (var path in JavaTestRepo.SourceFiles)
            lines.Add(J(new { k = "file", path, encoding = path == servicio ? "ISO-8859-1" : "UTF-8", errors = path == servicio ? 1 : 0 }));

        lines.Add(J(Type(JavaTestRepo.BeanPath, ["Bean"], "com.acme.Bean", "class", 6, 12, doc: "Año de la acción.")));
        lines.Add(J(Method(JavaTestRepo.BeanPath, ["Bean"], "com.acme.Bean", "getId", Line(JavaTestRepo.BeanPath, "getId()"), 11, [], [], [], "long", "long")));

        lines.Add(J(Type(JavaTestRepo.DaoPath, ["Dao"], "com.acme.dao.Dao", "interface", 5, 8, @abstract: true)));
        var daoTwo = Line(JavaTestRepo.DaoPath, "long id");
        var daoOne = Line(JavaTestRepo.DaoPath, "Hashtable c);");
        lines.Add(J(Method(JavaTestRepo.DaoPath, ["Dao"], "com.acme.dao.Dao", "consultar", daoTwo, daoTwo, [ht, "long"], ["Hashtable", "long"], ["c", "id"], "com.acme.Bean", "Bean", @abstract: true)));
        lines.Add(J(Method(JavaTestRepo.DaoPath, ["Dao"], "com.acme.dao.Dao", "consultar", daoOne, daoOne, [ht], ["Hashtable"], ["c"], "com.acme.Bean", "Bean", @abstract: true)));

        lines.Add(J(Type(JavaTestRepo.DaoImplPath, ["DaoImpl"], "com.acme.dao.DaoImpl", "class", 5, 17, interfaces: [Ref("com.acme.dao.Dao", "project")])));
        lines.Add(J(Method(JavaTestRepo.DaoImplPath, ["DaoImpl"], "com.acme.dao.DaoImpl", "consultar", 6, 8, [ht, "long"], ["Hashtable", "long"], ["c", "id"], "com.acme.Bean", "Bean")));
        lines.Add(J(Method(JavaTestRepo.DaoImplPath, ["DaoImpl"], "com.acme.dao.DaoImpl", "consultar", 10, 12, [ht], ["Hashtable"], ["c"], "com.acme.Bean", "Bean")));
        lines.Add(J(Type(JavaTestRepo.DaoImplPath, ["DaoImpl", "Cache"], "com.acme.dao.DaoImpl.Cache", "class", 14, 16)));
        lines.Add(J(Method(JavaTestRepo.DaoImplPath, ["DaoImpl", "Cache"], "com.acme.dao.DaoImpl.Cache", "put", 15, 15, ["java.lang.String"], ["String"], ["k"], "void", "void")));

        lines.Add(J(Type(JavaTestRepo.BasePath, ["Base"], "com.acme.negocio.Base", "class", 3, 5, @abstract: true)));
        lines.Add(J(Method(JavaTestRepo.BasePath, ["Base"], "com.acme.negocio.Base", "log", 4, 4, ["java.lang.String"], ["String"], ["m"], "void", "void", access: "protected")));

        lines.Add(J(Type(servicio, ["Servicio"], "com.acme.negocio.Servicio", "class", 6, JavaTestRepo.LineCount(servicio), Ref("com.acme.negocio.Base", "project"))));
        lines.Add(J(Method(servicio, ["Servicio"], "com.acme.negocio.Servicio", "ejecutar", Line(servicio, "ejecutar()"), 20, [], [], [], "void", "void", doc: "Añade la acción.", cyclomatic: 3)));
        lines.Add(J(Method(servicio, ["Servicio"], "com.acme.negocio.Servicio", "web", Line(servicio, "public void web"), 24,
            ["javax.servlet.http.HttpServletRequest"], ["HttpServletRequest"], ["r"], "void", "void")));
        lines.Add(J(Call(servicio, Line(servicio, "new DaoImpl()"), ejecutarKey, "com.acme.negocio.Servicio", "com.acme.dao.DaoImpl#DaoImpl()", "project", "new")));
        var twoArgs = Line(servicio, "1L)");
        lines.Add(J(Call(servicio, twoArgs, ejecutarKey, "com.acme.negocio.Servicio", "java.util.Hashtable#Hashtable()", "jdk", "new")));
        lines.Add(J(Call(servicio, twoArgs, ejecutarKey, "com.acme.negocio.Servicio", $"com.acme.dao.Dao#consultar({ht},long)", "project")));
        var oneArg = Line(servicio, "d.consultar(new java.util.Hashtable());");
        lines.Add(J(Call(servicio, oneArg, ejecutarKey, "com.acme.negocio.Servicio", "java.util.Hashtable#Hashtable()", "jdk", "new")));
        lines.Add(J(Call(servicio, oneArg, ejecutarKey, "com.acme.negocio.Servicio", $"com.acme.dao.Dao#consultar({ht})", "project")));
        lines.Add(J(Call(servicio, Line(servicio, "log(\"x\")"), ejecutarKey, "com.acme.negocio.Servicio", "com.acme.negocio.Base#log(java.lang.String)", "project")));
        var cacheLine = Line(servicio, "Cache()");
        lines.Add(J(Call(servicio, cacheLine, ejecutarKey, "com.acme.negocio.Servicio", "com.acme.dao.DaoImpl.Cache#Cache()", "project", "new")));
        lines.Add(J(Call(servicio, cacheLine, ejecutarKey, "com.acme.negocio.Servicio", "com.acme.dao.DaoImpl.Cache#put(java.lang.String)", "project")));
        lines.Add(J(Call(servicio, Line(servicio, "valueOf"), ejecutarKey, "com.acme.negocio.Servicio", "java.lang.String#valueOf(int)", "jdk")));
        var ifLine = Line(servicio, "isEmpty");
        lines.Add(J(Call(servicio, ifLine, ejecutarKey, "com.acme.negocio.Servicio", "java.lang.String#isEmpty()", "jdk")));
        lines.Add(J(Call(servicio, ifLine, ejecutarKey, "com.acme.negocio.Servicio", "java.lang.String#length()", "jdk")));
        lines.Add(J(Call(servicio, Line(servicio, "getSession"), "com.acme.negocio.Servicio#web(javax.servlet.http.HttpServletRequest)",
            "com.acme.negocio.Servicio", null, "unresolved", reason: "error-owner")));
        lines.Add(J(new { k = "diag", path = servicio, line = Line(servicio, "public void web"), msg = "package javax.servlet.http does not exist" }));

        var action = JavaTestRepo.ConsultarActionPath;
        lines.Add(J(Type(action, ["ConsultarAction"], "com.acme.web.ConsultarAction", "class", 3, 7)));
        lines.Add(J(Method(action, ["ConsultarAction"], "com.acme.web.ConsultarAction", "execute", 4, 6, [], [], [], "java.lang.String", "String")));
        var executeKey = "com.acme.web.ConsultarAction#execute()";
        lines.Add(J(Call(action, 5, executeKey, "com.acme.web.ConsultarAction", "com.acme.negocio.Servicio#Servicio()", "project", "new")));
        lines.Add(J(Call(action, 5, executeKey, "com.acme.web.ConsultarAction", "java.lang.Object#toString()", "jdk")));

        if (withReferences)
        {
            lines.AddRange(ReferenceLines());
            lines.Add(J(new
            {
                k = "summary", schema = 2, analyzerVersion = "2", javaVersion = "17.0.20.1", files = 6, parseMs = 120, analyzeMs = 340,
                scanMs = 15, heapPeakMb = 64, batches = 1, wallMs = 2100,
                calls = new Dictionary<string, int> { ["project"] = 7, ["jdk"] = 7, ["jar"] = 0, ["unresolved"] = 1 },
                unresolvedByReason = new Dictionary<string, int> { ["error-owner"] = 1 },
                missingPackages = new[] { "javax.servlet.http" },
                diagnostics = new Dictionary<string, int> { ["error"] = 1, ["warning"] = 0 },
                refs = new
                {
                    type = new Dictionary<string, int> { ["project"] = 8, ["jdk"] = 4, ["jar"] = 0, ["unresolved"] = 1 },
                    field = new Dictionary<string, int> { ["project"] = 1, ["jdk"] = 0, ["jar"] = 0, ["unresolved"] = 0 },
                    contexts = new Dictionary<string, int> { ["local"] = 1, ["new_implicit"] = 3, ["return"] = 4 },
                    records = 9,
                    selfExcluded = 0,
                    unresolvedByReason = new Dictionary<string, int> { ["error-type"] = 1 }
                },
                fields = 1
            }));
            return lines;
        }

        lines.Add(J(new
        {
            k = "summary", schema = 1, analyzerVersion = "1", javaVersion = "17.0.20.1", files = 6, parseMs = 120, analyzeMs = 340,
            scanMs = 15, heapPeakMb = 64, batches = 1, wallMs = 2100,
            calls = new Dictionary<string, int> { ["project"] = 7, ["jdk"] = 7, ["jar"] = 0, ["unresolved"] = 1 },
            unresolvedByReason = new Dictionary<string, int> { ["error-owner"] = 1 },
            missingPackages = new[] { "javax.servlet.http" },
            diagnostics = new Dictionary<string, int> { ["error"] = 1, ["warning"] = 0 }
        }));
        return lines;
    }

    /// <summary>Los registros <c>field</c> y <c>ref</c> de schema 2 para <see cref="JavaTestRepo"/>.</summary>
    private static List<string> ReferenceLines()
    {
        const string ht = "java.util.Hashtable";
        var bean = JavaTestRepo.BeanPath;
        var servicio = JavaTestRepo.ServicioPath;
        var ejecutarKey = "com.acme.negocio.Servicio#ejecutar()";
        var daoTwo = Line(JavaTestRepo.DaoPath, "long id");
        var daoOne = Line(JavaTestRepo.DaoPath, "Hashtable c);");
        return
        [
            J(Field(bean, ["Bean"], "com.acme.Bean", "id", Line(bean, "private long id;"), "long")),
            J(FieldRef(bean, "com.acme.Bean#getId()", "com.acme.Bean", "com.acme.Bean#id", "read", [Line(bean, "return id;")])),
            J(TypeRef(JavaTestRepo.DaoPath, $"com.acme.dao.Dao#consultar({ht},long)", "com.acme.dao.Dao", "com.acme.Bean", ["return"], [daoTwo])),
            J(TypeRef(JavaTestRepo.DaoPath, $"com.acme.dao.Dao#consultar({ht})", "com.acme.dao.Dao", "com.acme.Bean", ["return"], [daoOne])),
            J(TypeRef(JavaTestRepo.DaoImplPath, $"com.acme.dao.DaoImpl#consultar({ht},long)", "com.acme.dao.DaoImpl", "com.acme.Bean", ["return"], [6])),
            J(TypeRef(JavaTestRepo.DaoImplPath, $"com.acme.dao.DaoImpl#consultar({ht})", "com.acme.dao.DaoImpl", "com.acme.Bean", ["return"], [10])),
            J(TypeRef(servicio, ejecutarKey, "com.acme.negocio.Servicio", "com.acme.dao.Dao", ["local"], [Line(servicio, "Dao d =")])),
            J(TypeRef(servicio, ejecutarKey, "com.acme.negocio.Servicio", "com.acme.dao.DaoImpl", ["new_implicit"], [Line(servicio, "new DaoImpl()")])),
            J(TypeRef(servicio, ejecutarKey, "com.acme.negocio.Servicio", "com.acme.dao.DaoImpl.Cache", ["new_implicit"], [Line(servicio, "Cache()")])),
            J(TypeRef(JavaTestRepo.ConsultarActionPath, "com.acme.web.ConsultarAction#execute()", "com.acme.web.ConsultarAction",
                "com.acme.negocio.Servicio", ["new_implicit"], [5]))
        ];
    }

    public static string WriteResult(string directory, IEnumerable<string> lines)
    {
        var path = Path.Combine(directory, "java-analysis.jsonl");
        File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
        return path;
    }

    public static JavaGraphWriteRequest Request(string databasePath) => new()
    {
        DatabasePath = databasePath,
        AnalyzedFiles = JavaTestRepo.SourceFiles,
        TestRoots = ["test"],
        ConfigFingerprint = "cfg",
        StartedAt = DateTimeOffset.UtcNow
    };
}

public class JavaGraphWriterTests
{
    private static async Task<(JavaGraphWriteResult Result, CbmTestGraph Graph)> ApplyAsync(TempDirectory temp, bool withInterfaceMethodNodes = true,
        IEnumerable<string>? extraLines = null, bool withReferences = false, Func<CbmTestGraph, Task>? arrange = null)
    {
        var db = Path.Combine(temp.Path, "codebase.db");
        var graph = await JavaGraphFixture.CreateGraphAsync(db, withInterfaceMethodNodes);
        if (arrange != null)
            await arrange(graph);
        var lines = JavaGraphFixture.ResultLines(withReferences);
        if (extraLines != null)
            lines.InsertRange(lines.Count - 1, extraLines);
        var result = await JavaGraphWriter.ApplyAsync(JavaGraphFixture.Request(db),
            JavaAnalysisReader.Read(JavaGraphFixture.WriteResult(temp.Path, lines)), CancellationToken.None);
        return (result, graph);
    }

    private static Task<List<string>> CallTargetsAsync(CbmTestGraph graph, string sourceQn) =>
        graph.ListAsync("""
            SELECT t.qualified_name FROM edges e
            JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = 'CALLS' AND s.qualified_name = @qn ORDER BY t.qualified_name
            """, r => r.GetString(0), ("@qn", sourceQn));

    private static async Task<JsonObject> PropertiesAsync(CbmTestGraph graph, string qn) =>
        JsonNode.Parse((await graph.TextAsync("SELECT properties FROM nodes WHERE qualified_name = @qn", ("@qn", qn)))!)!.AsObject();

    private static Task<long> LineAsync(CbmTestGraph graph, string qn, string column = "start_line") =>
        graph.CountAsync($"SELECT {column} FROM nodes WHERE qualified_name = @qn", ("@qn", qn));

    [Fact]
    public async Task Overload_matching_the_cbm_line_keeps_the_qn_and_the_other_gets_a_suffix()
    {
        using var temp = new TempDirectory();
        var (_, graph) = await ApplyAsync(temp);
        using var _g = graph;

        // DaoImpl: cbm tenía la línea 10 (la de un parámetro): esa conserva el QN; la de dos se crea con sufijo.
        Assert.Equal(10, await LineAsync(graph, JavaGraphFixture.DaoImplConsultar));
        Assert.Equal(12, await LineAsync(graph, JavaGraphFixture.DaoImplConsultar, "line_end"));
        var kept = await PropertiesAsync(graph, JavaGraphFixture.DaoImplConsultar);
        Assert.Equal("Bean consultar(Hashtable c)", kept["signature"]!.GetValue<string>());
        Assert.Equal("[\"java.util.Hashtable\"]", kept["param_types"]!.ToJsonString());
        Assert.False(kept.ContainsKey("origin"));

        var overloadQn = JavaGraphFixture.DaoImplConsultar + "(Hashtable,long)";
        var overload = await PropertiesAsync(graph, overloadQn);
        Assert.Equal(JavaGraphFixture.DaoImplConsultar, overload["overload_of"]!.GetValue<string>());
        Assert.Equal("enricher", overload["origin"]!.GetValue<string>());
        Assert.Equal("[\"JavaAnalyzer\"]", overload["enriched_by"]!.ToJsonString());
        Assert.Equal("Bean consultar(Hashtable c, long id)", overload["signature"]!.GetValue<string>());
        Assert.Equal("[\"java.util.Hashtable\",\"long\"]", overload["param_types"]!.ToJsonString());
        Assert.Equal(6, await LineAsync(graph, overloadQn));
        Assert.Equal(6, await LineAsync(graph, overloadQn, "line_start"));
        Assert.Equal(8, await LineAsync(graph, overloadQn, "line_end"));
        Assert.Equal(JavaTestRepo.DaoImplPath, await graph.TextAsync("SELECT file FROM nodes WHERE qualified_name = @qn", ("@qn", overloadQn)));
        Assert.Equal(1, await graph.CountAsync("""
            SELECT COUNT(1) FROM edges e JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = 'DEFINES_METHOD' AND s.qualified_name = @type AND t.qualified_name = @qn
            """, ("@type", JavaGraphFixture.Qn(JavaTestRepo.DaoImplPath, "DaoImpl")), ("@qn", overloadQn)));

        // Dao (interfaz): la línea 7 conserva el QN del Method; la 6 va con sufijo.
        Assert.Equal(7, await LineAsync(graph, JavaGraphFixture.DaoConsultar));
        Assert.NotNull(await graph.NodeIdAsync(JavaGraphFixture.DaoConsultar + "(Hashtable,long)"));
    }

    [Fact]
    public async Task Cbm_calls_are_replaced_by_the_resolved_ones_and_interface_calls_hit_the_interface()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        var targets = await CallTargetsAsync(graph, JavaGraphFixture.Ejecutar);
        Assert.Equal(new[]
        {
            JavaGraphFixture.Qn(JavaTestRepo.BasePath, "Base.log"),
            JavaGraphFixture.DaoConsultar,
            JavaGraphFixture.DaoConsultar + "(Hashtable,long)",
            JavaGraphFixture.Qn(JavaTestRepo.DaoImplPath, "DaoImpl.Cache.put")
        }.OrderBy(q => q, StringComparer.Ordinal), targets);
        Assert.DoesNotContain(JavaGraphFixture.DaoImplConsultar, targets);

        // ConsultarAction.execute llamaba a Servicio.ejecutar según cbm; en realidad hace new Servicio() (constructor
        // implícito, sin nodo) y toString() del JDK, así que se queda sin CALLS.
        Assert.Empty(await CallTargetsAsync(graph, JavaGraphFixture.Qn(JavaTestRepo.ConsultarActionPath, "ConsultarAction.execute")));

        var edge = JsonNode.Parse((await graph.TextAsync("""
            SELECT e.properties FROM edges e JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = 'CALLS' AND s.qualified_name = @s AND t.qualified_name = @t
            """, ("@s", JavaGraphFixture.Ejecutar), ("@t", JavaGraphFixture.DaoConsultar)))!)!.AsObject();
        Assert.Equal("[\"JavaAnalyzer\"]", edge["enriched_by"]!.ToJsonString());
        Assert.Equal($"[{JavaTestRepo.LineOf(JavaTestRepo.ServicioPath, "d.consultar(new java.util.Hashtable());")}]", edge["lines"]!.ToJsonString());
        Assert.Equal(1, edge["count"]!.GetValue<int>());
        Assert.Equal("[\"call\"]", edge["kinds"]!.ToJsonString());

        // Solo quedan CALLS escritas por el analizador en los ficheros analizados; las aristas que no se sustituyen siguen.
        Assert.Equal(0, await graph.CountAsync($"SELECT COUNT(1) FROM edges WHERE type = 'CALLS' AND NOT {EnrichmentMarks.EnrichedByPredicate("'JavaAnalyzer'")}"));
        Assert.Equal(1, await graph.CountAsync("SELECT COUNT(1) FROM edges WHERE type = 'USAGE'"));
        Assert.Equal(1, await graph.CountAsync("SELECT COUNT(1) FROM edges WHERE type = 'DEFINES'"));
        Assert.Equal(4, result.Stats.CallEdges);
        Assert.Equal(3, result.Stats.ProjectCallsUnmatched);
        Assert.Equal(3, result.Stats.ProjectCallsUnmatchedByReason[JavaGraphWriter.ReasonImplicitConstructor]);
    }

    [Fact]
    public async Task Hierarchy_edges_go_only_to_project_types()
    {
        using var temp = new TempDirectory();
        var (_, graph) = await ApplyAsync(temp);
        using var _g = graph;

        var hierarchy = await graph.ListAsync("""
            SELECT e.type, s.name, t.name, e.properties FROM edges e
            JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type IN ('INHERITS', 'IMPLEMENTS') ORDER BY e.type
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));

        Assert.Equal(2, hierarchy.Count);
        Assert.Equal(("IMPLEMENTS", "DaoImpl", "Dao"), (hierarchy[0].Item1, hierarchy[0].Item2, hierarchy[0].Item3));
        Assert.Equal(("INHERITS", "Servicio", "Base"), (hierarchy[1].Item1, hierarchy[1].Item2, hierarchy[1].Item3));
        Assert.All(hierarchy, h => Assert.Contains("JavaAnalyzer", h.Item4));

        var bean = await PropertiesAsync(graph, JavaGraphFixture.Qn(JavaTestRepo.BeanPath, "Bean"));
        Assert.False(bean.ContainsKey("super_fqn"));
        Assert.Equal("Año de la acción.", bean["docstring"]!.GetValue<string>());
        var daoImpl = await PropertiesAsync(graph, JavaGraphFixture.Qn(JavaTestRepo.DaoImplPath, "DaoImpl"));
        Assert.Equal("[\"com.acme.dao.Dao\"]", daoImpl["interfaces_fqn"]!.ToJsonString());
        Assert.Equal("[\"implements Dao\"]", daoImpl["base_classes"]!.ToJsonString());
    }

    [Fact]
    public async Task Test_code_is_removed_with_its_edges_and_vectors()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        Assert.Equal(0, await graph.CountAsync("SELECT COUNT(1) FROM nodes WHERE file_path = 'test' OR file_path LIKE 'test/%'"));
        Assert.Equal(0, await graph.CountAsync("SELECT COUNT(1) FROM node_vectors"));
        Assert.Equal(0, await graph.CountAsync("""
            SELECT COUNT(1) FROM edges
            WHERE source_id NOT IN (SELECT id FROM nodes) OR target_id NOT IN (SELECT id FROM nodes)
            """));
        Assert.Equal(5, result.Outcome.NodesRemoved);
    }

    [Fact]
    public async Task Method_and_type_properties_are_completed_and_invalid_json_is_wrapped()
    {
        using var temp = new TempDirectory();
        var (_, graph) = await ApplyAsync(temp);
        using var _g = graph;

        var servicio = await PropertiesAsync(graph, JavaGraphFixture.Qn(JavaTestRepo.ServicioPath, "Servicio"));
        Assert.Equal("{\"base_classes\":[\"extends\nBase\"]}", servicio[EnrichmentMarks.RawPropertiesKey]!.GetValue<string>());
        Assert.Equal("com.acme.negocio.Servicio", servicio["fqn"]!.GetValue<string>());
        Assert.Equal("com.acme.negocio.Base", servicio["super_fqn"]!.GetValue<string>());
        Assert.Equal("class", servicio["kind"]!.GetValue<string>());

        var ejecutar = await PropertiesAsync(graph, JavaGraphFixture.Ejecutar);
        Assert.Equal("void ejecutar()", ejecutar["signature"]!.GetValue<string>());
        Assert.Equal("Añade la acción.", ejecutar["docstring"]!.GetValue<string>());
        Assert.Equal(3, ejecutar["cyclomatic"]!.GetValue<int>());
        Assert.Equal("public", ejecutar["accessibility"]!.GetValue<string>());
        Assert.False(ejecutar["is_static"]!.GetValue<bool>());
        Assert.False(ejecutar["is_constructor"]!.GetValue<bool>());
        Assert.Equal("void", ejecutar["return_type_fqn"]!.GetValue<string>());
        Assert.Equal("[\"JavaAnalyzer\"]", ejecutar["enriched_by"]!.ToJsonString());

        var log = await PropertiesAsync(graph, JavaGraphFixture.Qn(JavaTestRepo.BasePath, "Base.log"));
        Assert.Equal("protected", log["accessibility"]!.GetValue<string>());
        Assert.Equal("[\"java.lang.String\"]", log["param_types"]!.ToJsonString());
    }

    [Fact]
    public async Task The_pass_leaves_a_complete_enrichment_runs_row()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        var run = Assert.Single(await EnrichmentRuns.ListAsync(graph.Connection, CancellationToken.None));
        Assert.Equal(result.Outcome.RunId, run.Id);
        Assert.Equal(JavaGraphWriter.EnricherName, run.Enricher);
        Assert.Equal(JavaGraphWriter.EnricherVersion, run.EnricherVersion);
        Assert.Equal("17.0.20.1", run.ToolVersion);
        Assert.Equal($"{JavaGraphFixture.Project}|{JavaGraphFixture.IndexedAt}", run.IndexFingerprint);
        Assert.Equal("cfg", run.ConfigFingerprint);
        Assert.Equal(EnrichmentRuns.StatusOk, run.Status);
        Assert.True(run.DurationMs >= 0);

        var outcome = run.Outcome!;
        Assert.Equal(JavaTestRepo.SourceFiles.Count, outcome.FilesAnalyzed);
        Assert.Equal(0, outcome.FilesUnmatched);
        Assert.Equal(2, outcome.NodesCreated);
        Assert.Equal(15, outcome.NodesUpdated);
        Assert.Equal(5, outcome.NodesRemoved);
        Assert.Equal(4, outcome.CbmEdgesReplaced);
        Assert.Equal(8, outcome.EdgesCreated);
        Assert.Equal("{\"error-owner\":1,\"implicit_constructor\":3}", outcome.Unresolved);
        var diagnostics = JsonNode.Parse(outcome.Diagnostics!)!;
        Assert.Equal(1, diagnostics["error"]!.GetValue<long>());
        Assert.Equal(1, diagnostics["files_with_errors"]!.GetValue<int>());
        Assert.Equal("[\"javax.servlet.http\"]", diagnostics["missing_packages"]!.ToJsonString());
        var details = JsonNode.Parse(outcome.Details!)!;
        Assert.Equal(7, details["calls"]!["project"]!.GetValue<long>());
        Assert.Equal(2, details["methods"]!["overloads"]!.GetValue<int>());
        Assert.Equal(1, details["batches"]!.GetValue<int>());
        Assert.Equal(outcome with { RunId = run.Id }, result.Outcome);

        Assert.True(await EnrichmentRuns.HasSuccessfulRunAsync(graph.Connection, JavaGraphWriter.EnricherName, JavaGraphWriter.EnricherVersion,
            await EnrichmentRuns.IndexFingerprintAsync(graph.Connection, CancellationToken.None), "cfg", CancellationToken.None));
    }

    [Fact]
    public async Task With_matching_qns_the_run_row_records_no_cbm_drift()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        // Las dos sobrecargas se crean siempre (P8) y no cuentan como declaraciones sin casar.
        Assert.Equal(2, result.Stats.OverloadsCreated);
        Assert.Equal(0, result.Stats.TypesUnmatchedRatio);
        Assert.Equal(0, result.Stats.MethodsUnmatchedRatio);
        Assert.False(result.Stats.SuspectsCbmDrift);
        Assert.Null(Steps.LanguageEnricherStep.CbmDriftWarning(result.Stats));
        var match = JsonNode.Parse(result.Outcome.Details!)!["cbm_match"]!;
        Assert.False(match["drift_suspected"]!.GetValue<bool>());
        Assert.Equal(JavaGraphWriter.CbmDriftThreshold, match["threshold"]!.GetValue<double>());
    }

    [Fact]
    public async Task When_cbm_derives_its_qns_differently_the_created_nodes_do_not_hide_the_drift()
    {
        using var temp = new TempDirectory();
        var db = Path.Combine(temp.Path, "codebase.db");
        using var graph = await JavaGraphFixture.CreateGraphAsync(db);
        // Una versión de cbm que deriva los QN de otra forma: ninguno casa, y P9 crearía todos como nodos nuevos.
        using (var rename = graph.Connection.CreateCommand())
        {
            rename.CommandText = "UPDATE nodes SET qualified_name = 'v2.' || qualified_name WHERE label IN ('Class', 'Interface', 'Enum', 'Method', 'Function')";
            await rename.ExecuteNonQueryAsync();
        }

        var result = await JavaGraphWriter.ApplyAsync(JavaGraphFixture.Request(db),
            JavaAnalysisReader.Read(JavaGraphFixture.WriteResult(temp.Path, JavaGraphFixture.ResultLines())), CancellationToken.None);

        Assert.Equal(0, result.Stats.TypesMatched);
        Assert.True(result.Stats.TypesCreated > 0);
        Assert.Equal(1, result.Stats.TypesUnmatchedRatio);
        Assert.True(result.Stats.SuspectsCbmDrift);
        var run = Assert.Single(await EnrichmentRuns.ListAsync(graph.Connection, CancellationToken.None));
        Assert.Equal(EnrichmentRuns.StatusOk, run.Status);
        var match = JsonNode.Parse(run.Outcome!.Details!)!["cbm_match"]!;
        Assert.True(match["drift_suspected"]!.GetValue<bool>());
        Assert.Equal(1, match["types_unmatched_ratio"]!.GetValue<double>());
        Assert.Equal(1, match["methods_unmatched_ratio"]!.GetValue<double>());

        var warning = Steps.LanguageEnricherStep.CbmDriftWarning(result.Stats);
        Assert.NotNull(warning);
        Assert.StartsWith("⚠️ Analizador Java: no casan con un nodo de cbm 100,00 % de los tipos", warning);
        Assert.EndsWith("Los nombres de cbm no casan con los del analizador: ¿ha cambiado la versión de cbm?", warning);
    }

    /// <summary>Quita del grafo sintético todos los nodos de cbm de un fichero, como si cbm no lo hubiera indexado.</summary>
    private static async Task UnindexAsync(CbmTestGraph graph, string path)
    {
        using var delete = graph.Connection.CreateCommand();
        delete.CommandText = """
            DELETE FROM edges WHERE source_id IN (SELECT id FROM nodes WHERE file_path = @p) OR target_id IN (SELECT id FROM nodes WHERE file_path = @p);
            DELETE FROM nodes WHERE file_path = @p;
            """;
        delete.Parameters.AddWithValue("@p", path);
        await delete.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// H-9 tras la prueba M: los tipos de un fichero que cbm no indexó (ningún nodo suyo en ese <c>file_path</c>) no pueden
    /// casar, y el aviso lo atribuye a eso y no a un cambio de versión de cbm; con una ruta absoluta de 260 caracteres o más
    /// añade la pista del MAX_PATH. Los recuentos de las dos causas quedan en <c>details.cbm_match</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Types_of_a_file_cbm_did_not_index_are_not_blamed_on_a_cbm_version_change(bool longPath)
    {
        using var temp = new TempDirectory();
        var db = Path.Combine(temp.Path, "codebase.db");
        using var graph = await JavaGraphFixture.CreateGraphAsync(db);
        await UnindexAsync(graph, JavaTestRepo.BeanPath);
        // La raíz no tiene que existir: solo se mide la longitud de la ruta absoluta de cada fichero.
        var root = longPath ? Path.Combine(temp.Path, new string('r', 260)) : temp.Path;

        var result = await JavaGraphWriter.ApplyAsync(JavaGraphFixture.Request(db) with { RepositoryPath = root },
            JavaAnalysisReader.Read(JavaGraphFixture.WriteResult(temp.Path, JavaGraphFixture.ResultLines())), CancellationToken.None);

        var stats = result.Stats;
        Assert.Equal(1, stats.UnindexedFiles);
        Assert.Equal(1, result.Outcome.FilesUnmatched);
        Assert.Equal(longPath ? 1 : 0, stats.UnindexedLongPathFiles);
        Assert.Equal((1, 1), (stats.TypesCreated, stats.TypesCreatedInUnindexedFiles));
        Assert.Equal(1, stats.MethodsCreatedInUnindexedFiles);
        Assert.Equal((0, 0), (stats.TypesCreatedNameMismatch, stats.MethodsCreatedNameMismatch));
        // Un tipo de siete sin casar supera el umbral, pero no es deriva de los QN de cbm.
        Assert.True(stats.ExceedsUnmatchedThreshold);
        Assert.False(stats.SuspectsCbmDrift);

        var match = JsonNode.Parse(result.Outcome.Details!)!["cbm_match"]!;
        Assert.Equal(1, match["unindexed_files"]!.GetValue<int>());
        Assert.Equal(longPath ? 1 : 0, match["unindexed_files_long_path"]!.GetValue<int>());
        Assert.Equal(1, match["types_created_unindexed_file"]!.GetValue<int>());
        Assert.Equal(0, match["types_created_name_mismatch"]!.GetValue<int>());
        Assert.Equal(1, match["methods_created_unindexed_file"]!.GetValue<int>());
        Assert.Equal(0, match["methods_created_name_mismatch"]!.GetValue<int>());
        Assert.False(match["drift_suspected"]!.GetValue<bool>());

        var warning = Steps.LanguageEnricherStep.CbmDriftWarning(stats);
        Assert.NotNull(warning);
        Assert.StartsWith("⚠️ Analizador Java: cbm no indexó 1 fichero .java que el analizador sí leyó (no tiene ningún nodo suyo); "
            + "sus declaraciones se han creado como nodos nuevos: 1 tipo y 1 método sin contar sobrecargas.", warning);
        Assert.DoesNotContain("versión de cbm", warning);
        Assert.Contains("En los ficheros que cbm sí indexó los nombres casan: no casan 0,00 % de los tipos (0 de 6)", warning);
        Assert.Equal(longPath, warning.Contains("MAX_PATH", StringComparison.Ordinal));
        if (longPath)
            Assert.Contains("Ese fichero tiene una ruta absoluta de 260 caracteres o más, el límite MAX_PATH de Windows, que cbm no indexa: "
                + "copia el repositorio a una ruta más corta.", warning);
    }

    [Fact]
    public async Task With_unindexed_files_and_renamed_qns_the_warning_gives_both_causes()
    {
        using var temp = new TempDirectory();
        var db = Path.Combine(temp.Path, "codebase.db");
        using var graph = await JavaGraphFixture.CreateGraphAsync(db);
        await UnindexAsync(graph, JavaTestRepo.BeanPath);
        using (var rename = graph.Connection.CreateCommand())
        {
            rename.CommandText = "UPDATE nodes SET qualified_name = 'v2.' || qualified_name WHERE label IN ('Class', 'Interface', 'Enum', 'Method', 'Function')";
            await rename.ExecuteNonQueryAsync();
        }

        var result = await JavaGraphWriter.ApplyAsync(JavaGraphFixture.Request(db),
            JavaAnalysisReader.Read(JavaGraphFixture.WriteResult(temp.Path, JavaGraphFixture.ResultLines())), CancellationToken.None);

        Assert.True(result.Stats.SuspectsCbmDrift);
        Assert.Null(result.Stats.UnindexedLongPathFiles);
        Assert.Null(JsonNode.Parse(result.Outcome.Details!)!["cbm_match"]!["unindexed_files_long_path"]);
        var warning = Steps.LanguageEnricherStep.CbmDriftWarning(result.Stats)!;
        Assert.StartsWith("⚠️ Analizador Java: cbm no indexó 1 fichero .java", warning);
        Assert.Contains(" No casan con un nodo de cbm en los ficheros que cbm sí indexó 100,00 % de los tipos (6 de 6)", warning);
        Assert.EndsWith("¿ha cambiado la versión de cbm?", warning);
        Assert.DoesNotContain("MAX_PATH", warning);
    }

    [Theory]
    [InlineData(95, 5, 100, 0, false)]  // justo en el umbral: no avisa
    [InlineData(94, 6, 100, 0, true)]   // tipos por encima
    [InlineData(100, 0, 90, 20, true)]  // métodos: 20 creados, 10 de ellos sobrecargas → 10 de 100
    [InlineData(100, 0, 95, 15, false)] // métodos: 15 creados, 10 sobrecargas → 5 de 100
    [InlineData(0, 0, 0, 0, false)]     // sin declaraciones
    public void The_drift_ratio_leaves_out_the_overloads(int typesMatched, int typesCreated, int methodsMatched, int methodsCreated, bool drift)
    {
        var stats = new JavaGraphStats
        {
            TypesMatched = typesMatched,
            TypesCreated = typesCreated,
            MethodsMatched = methodsMatched,
            MethodsCreated = methodsCreated,
            OverloadsCreated = methodsCreated >= 10 ? 10 : 0
        };

        Assert.Equal(drift, stats.SuspectsCbmDrift);
        Assert.Equal(drift, Steps.LanguageEnricherStep.CbmDriftWarning(stats) != null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_second_pass_leaves_the_same_graph(bool withReferences)
    {
        using var temp = new TempDirectory();
        var (first, graph) = await ApplyAsync(temp, withReferences: withReferences);
        using var _g = graph;

        // Las propiedades de las aristas enteras (líneas, count, contextos); las de los nodos creados llevan enriched_at.
        async Task<List<string>> SnapshotAsync() => [
            .. await graph.ListAsync("SELECT label || ' ' || qualified_name || ' ' || start_line || '-' || end_line FROM nodes ORDER BY 1", r => r.GetString(0)),
            .. await graph.ListAsync("""
                SELECT e.type || ' ' || s.qualified_name || ' -> ' || t.qualified_name || ' ' || COALESCE(e.properties, '')
                FROM edges e JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id ORDER BY 1
                """, r => r.GetString(0)),
            .. await graph.ListAsync("SELECT qualified_name || ' ' || properties FROM nodes WHERE label = 'Field' ORDER BY 1", r => r.GetString(0))
        ];

        var before = await SnapshotAsync();
        var second = await JavaGraphWriter.ApplyAsync(JavaGraphFixture.Request(graph.DatabasePath),
            JavaAnalysisReader.Read(Path.Combine(temp.Path, "java-analysis.jsonl")), CancellationToken.None);

        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(first.Outcome.NodesCreated, second.Outcome.NodesCreated);
        Assert.Equal(first.Outcome.NodesUpdated, second.Outcome.NodesUpdated);
        Assert.Equal(first.Outcome.EdgesCreated, second.Outcome.EdgesCreated);
        Assert.Equal(0, second.Outcome.CbmEdgesReplaced);
        Assert.Equal(0, second.Outcome.NodesRemoved);
        Assert.Equal(first.Outcome.NodesCreated, second.Stats.PreviousNodesRemoved);
        Assert.Equal(2, (await EnrichmentRuns.ListAsync(graph.Connection, CancellationToken.None)).Count);
        Assert.Equal((first.Stats.FieldsMatched, first.Stats.UsesTypeEdges, first.Stats.ReadsEdges, first.Stats.WritesEdges),
            (second.Stats.FieldsMatched, second.Stats.UsesTypeEdges, second.Stats.ReadsEdges, second.Stats.WritesEdges));
        // La segunda pasada ya no encuentra USAGE de cbm que sustituir: la primera las retiró.
        Assert.False(second.Stats.CbmEdgesReplacedByType.ContainsKey("USAGE"));
    }

    [Fact]
    public async Task Interface_methods_fall_back_to_the_cbm_function_node()
    {
        using var temp = new TempDirectory();
        var (_, graph) = await ApplyAsync(temp, withInterfaceMethodNodes: false);
        using var _g = graph;

        var function = await PropertiesAsync(graph, JavaGraphFixture.DaoFunction);
        Assert.Equal("Bean consultar(Hashtable c)", function["signature"]!.GetValue<string>());
        Assert.True(function["is_abstract"]!.GetValue<bool>());

        var overload = await PropertiesAsync(graph, JavaGraphFixture.DaoConsultar + "(Hashtable,long)");
        Assert.Equal(JavaGraphFixture.DaoFunction, overload["overload_of"]!.GetValue<string>());
        Assert.Contains(JavaGraphFixture.DaoFunction, await CallTargetsAsync(graph, JavaGraphFixture.Ejecutar));
    }

    [Fact]
    public async Task Calls_outside_methods_leave_from_the_type_as_initializer()
    {
        using var temp = new TempDirectory();
        var initializer = JsonSerializer.Serialize(new
        {
            k = "call", path = JavaTestRepo.BeanPath, line = 7, from = (string?)null, fromType = "com.acme.Bean",
            to = "com.acme.negocio.Base#log(java.lang.String)", origin = "project", kind = "call", reason = (string?)null
        });
        var (_, graph) = await ApplyAsync(temp, extraLines: [initializer]);
        using var _g = graph;

        var properties = await graph.TextAsync("""
            SELECT e.properties FROM edges e JOIN nodes s ON s.id = e.source_id
            WHERE e.type = 'CALLS' AND s.qualified_name = @qn
            """, ("@qn", JavaGraphFixture.Qn(JavaTestRepo.BeanPath, "Bean")));
        Assert.Equal("initializer", JsonNode.Parse(properties!)!["context"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cbm_quirks_enum_typed_as_function_field_collisions_and_nodeless_calls()
    {
        using var temp = new TempDirectory();
        var db = Path.Combine(temp.Path, "codebase.db");
        const string estado = "src/com/acme/Estado.java";
        const string persona = "src/com/acme/Persona.java";
        using var graph = await CbmTestGraph.CreateAsync(db, JavaGraphFixture.Project, JavaGraphFixture.IndexedAt);
        var enumQn = JavaGraphFixture.Qn(estado, "Estado");
        var valorQn = JavaGraphFixture.Qn(estado, "valor");
        // El constructor del enum, emitido como Function en <prefijo>.Estado, pisó el nodo del tipo; valor() solo es Function.
        await graph.NodeAsync("Function", "Estado", enumQn, estado, 3, 10);
        await graph.NodeAsync("Function", "valor", valorQn, estado, 7, 9);
        await graph.NodeAsync("Class", "Persona", JavaGraphFixture.Qn(persona, "Persona"), persona, 1, 9);
        await graph.NodeAsync("Field", "nombre", JavaGraphFixture.Qn(persona, "Persona.nombre"), persona, 2, 2, "{\"return_type\":\"String\"}");

        var lines = new List<string>
        {
            JavaGraphFixture.J(new { k = "file", path = estado, encoding = "UTF-8", errors = 0 }),
            JavaGraphFixture.J(new { k = "file", path = persona, encoding = "UTF-8", errors = 0 }),
            JavaGraphFixture.J(JavaGraphFixture.Type(estado, ["Estado"], "com.acme.Estado", "enum", 3, 10)),
            JavaGraphFixture.J(JavaGraphFixture.Method(estado, ["Estado"], "com.acme.Estado", "Estado", 5, 5, ["int"], ["int"], ["v"], null, null,
                access: "private", ctor: true)),
            JavaGraphFixture.J(JavaGraphFixture.Method(estado, ["Estado"], "com.acme.Estado", "valor", 7, 9, [], [], [], "int", "int")),
            JavaGraphFixture.J(JavaGraphFixture.Type(persona, ["Persona"], "com.acme.Persona", "class", 1, 9)),
            JavaGraphFixture.J(JavaGraphFixture.Method(persona, ["Persona"], "com.acme.Persona", "nombre", 4, 6, [], [], [], "java.lang.String", "String")),
            JavaGraphFixture.J(JavaGraphFixture.Call(estado, 8, "com.acme.Estado#valor()", "com.acme.Estado", "com.acme.Estado#Estado(int)", "project")),
            JavaGraphFixture.J(JavaGraphFixture.Call(estado, 8, "com.acme.Estado#valor()", "com.acme.Estado", "com.acme.Estado$1Local#go()", "project")),
            JavaGraphFixture.J(JavaGraphFixture.Call(estado, 9, "com.acme.Estado#valor()", "com.acme.Estado", null, "project", reason: "CLASS")),
            JavaGraphFixture.J(JavaGraphFixture.Call(persona, 5, "com.acme.Persona#nombre()", "com.acme.Persona", "com.acme.Estado#valor()", "project")),
            JavaGraphFixture.J(new { k = "diag", path = "src/com/acme/Roto.java", line = (int?)null, msg = "unmappable character" }),
            JavaGraphFixture.J(new { k = "diag", path = (string?)null, line = (int?)null, msg = "warning: [options] bootstrap" }),
            JavaGraphFixture.J(new { k = "summary", schema = 1, javaVersion = "17.0.20.1", files = 2, batches = 1, wallMs = 10 })
        };

        var result = await JavaGraphWriter.ApplyAsync(JavaGraphFixture.Request(db) with { AnalyzedFiles = [estado, persona] },
            JavaAnalysisReader.Read(JavaGraphFixture.WriteResult(temp.Path, lines)), CancellationToken.None);

        // El tipo casa aunque su label sea Function, y no se cambia.
        Assert.Equal("Function", await graph.TextAsync("SELECT label FROM nodes WHERE qualified_name = @qn", ("@qn", enumQn)));
        var enumProperties = JsonNode.Parse((await graph.TextAsync("SELECT properties FROM nodes WHERE qualified_name = @qn", ("@qn", enumQn)))!)!;
        Assert.Equal("enum", enumProperties["kind"]!.GetValue<string>());
        Assert.Null(enumProperties["is_constructor"]);

        // El constructor no se ancla en el nodo del tipo: se crea como Method con el nombre de la clase.
        var ctorQn = enumQn + ".Estado";
        var ctor = JsonNode.Parse((await graph.TextAsync(
            "SELECT properties FROM nodes WHERE qualified_name = @qn AND label = 'Method'", ("@qn", ctorQn)))!)!;
        Assert.True(ctor["is_constructor"]!.GetValue<bool>());
        Assert.Equal("Estado(int v)", ctor["signature"]!.GetValue<string>());
        Assert.Null(ctor["return_type_fqn"]);
        Assert.Equal([ctorQn], await graph.ListAsync("""
            SELECT t.qualified_name FROM edges e JOIN nodes t ON t.id = e.target_id
            WHERE e.type = 'CALLS' AND e.source_id = (SELECT id FROM nodes WHERE qualified_name = @qn)
            """, r => r.GetString(0), ("@qn", valorQn)));

        // valor() solo existe como Function: hace de Method del grupo.
        var valor = JsonNode.Parse((await graph.TextAsync("SELECT properties FROM nodes WHERE qualified_name = @qn", ("@qn", valorQn)))!)!;
        Assert.Equal("int valor()", valor["signature"]!.GetValue<string>());

        // El QN de Persona.nombre() lo ocupa un Field: no casa y no se crea.
        Assert.Equal(1, result.Stats.MethodsUnmatched);
        Assert.Equal(0, await graph.CountAsync("SELECT COUNT(1) FROM nodes WHERE qualified_name LIKE '%Persona.nombre(%'"));
        Assert.Equal("{\"return_type\":\"String\"}", await graph.TextAsync("SELECT properties FROM nodes WHERE label = 'Field'"));

        Assert.Equal(1, result.Stats.ProjectCallsUnmatchedByReason[JavaGraphWriter.ReasonSourceWithoutNode]);
        Assert.Equal(1, result.Stats.ProjectCallsUnmatchedByReason[JavaGraphWriter.ReasonTargetWithoutNode]);
        Assert.Equal(2, JsonNode.Parse(result.Outcome.Unresolved!)![JavaGraphWriter.ReasonTargetWithoutNode]!.GetValue<long>());
        Assert.Equal(1, result.Stats.UnreadableFiles);
        Assert.Equal(1, JsonNode.Parse(result.Outcome.Diagnostics!)!["unreadable_files"]!.GetValue<int>());
        Assert.Equal(2, result.Outcome.FilesAnalyzed);
    }

    [Fact]
    public async Task Incomplete_result_changes_nothing()
    {
        using var temp = new TempDirectory();
        var db = Path.Combine(temp.Path, "codebase.db");
        using var graph = await JavaGraphFixture.CreateGraphAsync(db);
        var lines = JavaGraphFixture.ResultLines();
        lines.RemoveAt(lines.Count - 1);
        var nodes = await graph.CountAsync("SELECT COUNT(1) FROM nodes");
        var edges = await graph.CountAsync("SELECT COUNT(1) FROM edges");

        await Assert.ThrowsAsync<InvalidDataException>(() => JavaGraphWriter.ApplyAsync(JavaGraphFixture.Request(db),
            JavaAnalysisReader.Read(JavaGraphFixture.WriteResult(temp.Path, lines)), CancellationToken.None));

        Assert.Equal(nodes, await graph.CountAsync("SELECT COUNT(1) FROM nodes"));
        Assert.Equal(edges, await graph.CountAsync("SELECT COUNT(1) FROM edges"));
        Assert.Empty(await EnrichmentRuns.ListAsync(graph.Connection, CancellationToken.None));
    }

    [Fact]
    public async Task Never_writes_to_the_cbm_cache()
    {
        var cacheDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "codebase-memory-mcp", $"no-existe-{Guid.NewGuid():N}.db");

        await Assert.ThrowsAsync<InvalidOperationException>(() => JavaGraphWriter.ApplyAsync(
            JavaGraphFixture.Request(cacheDb), JavaGraphFixture.ResultLines().Select(l => JavaAnalysisReader.Parse(l)!), CancellationToken.None));
        Assert.False(File.Exists(cacheDb));
    }

    // ---------------------------------------------------------------- campos y referencias (schema 2, Q5-Q8)

    private static Task<List<(string Target, string Properties)>> EdgesFromAsync(CbmTestGraph graph, string type, string sourceQn) =>
        graph.ListAsync("""
            SELECT t.qualified_name, e.properties FROM edges e
            JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = @type AND s.qualified_name = @qn ORDER BY t.qualified_name
            """, r => (r.GetString(0), r.GetString(1)), ("@type", type), ("@qn", sourceQn));

    private static async Task<JsonObject> EdgeAsync(CbmTestGraph graph, string type, string sourceQn, string targetQn) =>
        JsonNode.Parse(Assert.Single(await EdgesFromAsync(graph, type, sourceQn), e => e.Target == targetQn).Properties)!.AsObject();

    [Fact]
    public async Task A_field_matches_the_cbm_field_whose_name_carries_the_initializer()
    {
        using var temp = new TempDirectory();
        var servicio = JavaTestRepo.ServicioPath;
        var contadorQn = JavaGraphFixture.Qn(servicio, "Servicio.contador = 0");
        var codigosQn = JavaGraphFixture.Qn(servicio, "Servicio.CODIGOS[]={\"1\"}");
        var avisoQn = JavaGraphFixture.Qn(servicio, "Servicio.aviso\t\t= true");
        var (result, graph) = await ApplyAsync(temp, withReferences: true, arrange: async g =>
        {
            var parent = "{\"parent_class\":\"" + JavaGraphFixture.Qn(servicio, "Servicio") + "\"}";
            await g.NodeAsync("Field", "contador = 0", contadorQn, servicio, 7, 7, parent);
            await g.NodeAsync("Field", "CODIGOS[]={\"1\"}", codigosQn, servicio, 8, 8, parent);
            await g.NodeAsync("Field", "aviso\t\t= true", avisoQn, servicio, 9, 9, parent);
            // Otro campo que empieza igual no debe casar con «contador».
            await g.NodeAsync("Field", "contadorTotal = 1", JavaGraphFixture.Qn(servicio, "Servicio.contadorTotal = 1"), servicio, 10, 10, parent);
        }, extraLines:
        [
            JavaGraphFixture.J(JavaGraphFixture.Field(servicio, ["Servicio"], "com.acme.negocio.Servicio", "contador", 7, "int", access: "protected",
                @static: true, doc: "Veces que se ha ejecutado.")),
            JavaGraphFixture.J(JavaGraphFixture.Field(servicio, ["Servicio"], "com.acme.negocio.Servicio", "CODIGOS", 8, "java.lang.String[]",
                access: "public", @static: true, final: true)),
            JavaGraphFixture.J(JavaGraphFixture.Field(servicio, ["Servicio"], "com.acme.negocio.Servicio", "aviso", 9, "boolean"))
        ]);
        using var _g = graph;

        // El nodo casado conserva su QN (con el inicializador) y se le completan las propiedades.
        var contador = await PropertiesAsync(graph, contadorQn);
        Assert.Equal("contador", contador["field_name"]!.GetValue<string>());
        Assert.Equal("int", contador["type_fqn"]!.GetValue<string>());
        Assert.Equal("protected", contador["accessibility"]!.GetValue<string>());
        Assert.True(contador["is_static"]!.GetValue<bool>());
        Assert.False(contador["is_final"]!.GetValue<bool>());
        Assert.Equal("Veces que se ha ejecutado.", contador["docstring"]!.GetValue<string>());
        Assert.Equal("[\"JavaAnalyzer\"]", contador["enriched_by"]!.ToJsonString());
        Assert.Equal(JavaGraphFixture.Qn(servicio, "Servicio"), contador["parent_class"]!.GetValue<string>());
        Assert.False(contador.ContainsKey("origin"));
        Assert.True((await PropertiesAsync(graph, codigosQn))["is_final"]!.GetValue<bool>());
        Assert.Equal("aviso", (await PropertiesAsync(graph, avisoQn))["field_name"]!.GetValue<string>());
        Assert.False((await PropertiesAsync(graph, JavaGraphFixture.Qn(servicio, "Servicio.contadorTotal = 1"))).ContainsKey("field_name"));

        // Bean.id casa con su Field; el Variable gemelo no se toca.
        Assert.Equal("long", (await PropertiesAsync(graph, JavaGraphFixture.BeanId))["type_fqn"]!.GetValue<string>());
        Assert.Equal("{}", await graph.TextAsync("SELECT properties FROM nodes WHERE label = 'Variable'"));

        Assert.Equal((4, 0, 0), (result.Stats.FieldsMatched, result.Stats.FieldsCreated, result.Stats.FieldsUnmatched));
        // contadorTotal y los tres campos de Servicio del fixture, que el resultado sintético no declara.
        Assert.Equal(4, result.Stats.CbmFieldsUnmatched);
        // Ningún Field creado: los que no llevan inicializador en el QN son todos del fixture.
        Assert.Equal(0, await graph.CountAsync(
            "SELECT COUNT(1) FROM nodes WHERE label = 'Field' AND qualified_name NOT LIKE '%=%' AND qualified_name NOT IN (@id, @pendiente, @usos)",
            ("@id", JavaGraphFixture.BeanId), ("@pendiente", JavaGraphFixture.PendienteField), ("@usos", JavaGraphFixture.UsosField)));
        var details = JsonNode.Parse(result.Outcome.Details!)!;
        Assert.Equal(4, details["fields"]!["matched"]!.GetValue<int>());
        Assert.Equal(0, details["cbm_match"]!["fields_unmatched_ratio"]!.GetValue<double>());
        Assert.False(result.Stats.SuspectsCbmDrift);
    }

    [Fact]
    public async Task A_field_without_a_cbm_node_is_created_under_its_type_unless_its_qn_is_taken()
    {
        using var temp = new TempDirectory();
        var bean = JavaTestRepo.BeanPath;
        var (result, graph) = await ApplyAsync(temp, withReferences: true, extraLines:
        [
            JavaGraphFixture.J(JavaGraphFixture.Field(bean, ["Bean"], "com.acme.Bean", "nombre", 8, "java.lang.String", doc: "Nombre.")),
            // El QN <Bean>.getId lo ocupa el Method: P9, ni casa ni se crea.
            JavaGraphFixture.J(JavaGraphFixture.Field(bean, ["Bean"], "com.acme.Bean", "getId", 8, "int")),
            JavaGraphFixture.J(JavaGraphFixture.Field("src/com/acme/Otro.java", ["Otro"], "com.acme.Otro", "x", 3, "int"))
        ]);
        using var _g = graph;

        var createdQn = JavaGraphFixture.Qn(bean, "Bean.nombre");
        var created = await PropertiesAsync(graph, createdQn);
        Assert.Equal("enricher", created["origin"]!.GetValue<string>());
        Assert.Equal("[\"JavaAnalyzer\"]", created["enriched_by"]!.ToJsonString());
        Assert.Equal("java.lang.String", created["type_fqn"]!.GetValue<string>());
        Assert.Equal(JavaGraphFixture.BeanQn, created["parent_class"]!.GetValue<string>());
        Assert.Equal("Field", await graph.TextAsync("SELECT label FROM nodes WHERE qualified_name = @qn", ("@qn", createdQn)));
        Assert.Equal("nombre", await graph.TextAsync("SELECT name FROM nodes WHERE qualified_name = @qn", ("@qn", createdQn)));
        Assert.Equal(8, await LineAsync(graph, createdQn));
        Assert.Equal(bean, await graph.TextAsync("SELECT file_path FROM nodes WHERE qualified_name = @qn", ("@qn", createdQn)));
        var defines = await EdgeAsync(graph, "DEFINES", JavaGraphFixture.BeanQn, createdQn);
        Assert.Equal("[\"JavaAnalyzer\"]", defines["enriched_by"]!.ToJsonString());
        Assert.Equal("Method", await graph.TextAsync("SELECT label FROM nodes WHERE qualified_name = @qn", ("@qn", JavaGraphFixture.GetId)));

        var stats = result.Stats;
        Assert.Equal((1, 1, 2), (stats.FieldsMatched, stats.FieldsCreated, stats.FieldsUnmatched));
        Assert.Equal(1, stats.FieldsUnmatchedByReason[JavaGraphWriter.ReasonQualifiedNameTaken]);
        Assert.Equal(1, stats.FieldsUnmatchedByReason[JavaGraphWriter.ReasonOwnerWithoutNode]);
        Assert.Equal(1, stats.DefinesFieldEdges);
        Assert.Equal(0, stats.FieldsCreatedInUnindexedFiles);
        Assert.Equal(1, stats.FieldsCreatedNameMismatch);
        Assert.Equal(0.5, stats.FieldsUnmatchedRatio);
        // Un campo de dos sin casar en ficheros que cbm indexó: supera el umbral y apunta a cbm.
        Assert.True(stats.SuspectsCbmDrift);
        var warning = Steps.LanguageEnricherStep.CbmDriftWarning(stats)!;
        Assert.Contains("y 50,00 % de los campos (1 de 2)", warning);
        var details = JsonNode.Parse(result.Outcome.Details!)!;
        Assert.Equal(1, details["fields"]!["unmatched_by_reason"]![JavaGraphWriter.ReasonQualifiedNameTaken]!.GetValue<long>());
        Assert.Equal(1, details["cbm_match"]!["fields_created_name_mismatch"]!.GetValue<int>());
        Assert.Equal(1, details["edges"]!["DEFINES"]!.GetValue<int>());
    }

    /// <summary>
    /// cbm solo guarda como <c>Field</c> los campos de clase (repo objetivo: 44.917 de 44.940 casan). Las constantes de enum
    /// las deja como <c>Variable</c> en <c>&lt;QN del tipo&gt;.&lt;constante&gt;</c>, que hace de ancla; los campos de
    /// interfaz o de enum no tienen nodo y se crean sin contar como deriva.
    /// </summary>
    [Fact]
    public async Task Enum_constants_anchor_on_the_cbm_variable_and_interface_constants_are_created_without_drift()
    {
        using var temp = new TempDirectory();
        const string color = "src/com/acme/Color.java";
        var colorQn = JavaGraphFixture.Qn(color, "Color");
        var rojoQn = JavaGraphFixture.Qn(color, "Color.ROJO");
        var twinQn = JavaGraphFixture.Qn(color, "ROJO");
        var (result, graph) = await ApplyAsync(temp, withReferences: true, arrange: async g =>
        {
            await g.NodeAsync("Enum", "Color", colorQn, color, 3, 6);
            await g.NodeAsync("Variable", "ROJO", rojoQn, color, 4, 4);
            await g.NodeAsync("Variable", "ROJO", twinQn, color, 4, 4);
        }, extraLines:
        [
            JavaGraphFixture.J(new { k = "file", path = color, encoding = "UTF-8", errors = 0 }),
            JavaGraphFixture.J(JavaGraphFixture.Type(color, ["Color"], "com.acme.Color", "enum", 3, 6)),
            JavaGraphFixture.J(JavaGraphFixture.Field(color, ["Color"], "com.acme.Color", "ROJO", 4, "com.acme.Color", access: "public",
                @static: true, final: true, enumConstant: true)),
            JavaGraphFixture.J(JavaGraphFixture.Field(color, ["Color"], "com.acme.Color", "codigo", 5, "int")),
            JavaGraphFixture.J(JavaGraphFixture.Field(JavaTestRepo.DaoPath, ["Dao"], "com.acme.dao.Dao", "MAX", 6, "int", access: "public",
                @static: true, final: true)),
            JavaGraphFixture.J(JavaGraphFixture.FieldRef(JavaTestRepo.ServicioPath, "com.acme.negocio.Servicio#ejecutar()", "com.acme.negocio.Servicio",
                "com.acme.Color#ROJO", "read", [12]))
        ]);
        using var _g = graph;

        Assert.Equal("Variable", await graph.TextAsync("SELECT label FROM nodes WHERE qualified_name = @qn", ("@qn", rojoQn)));
        var rojo = await PropertiesAsync(graph, rojoQn);
        Assert.Equal(("ROJO", "com.acme.Color"), (rojo["field_name"]!.GetValue<string>(), rojo["type_fqn"]!.GetValue<string>()));
        Assert.Equal("{}", await graph.TextAsync("SELECT properties FROM nodes WHERE qualified_name = @qn", ("@qn", twinQn)));
        Assert.Equal("[12]", (await EdgeAsync(graph, "READS", JavaGraphFixture.Ejecutar, rojoQn))["lines"]!.ToJsonString());
        Assert.NotNull(await graph.NodeIdAsync(JavaGraphFixture.Qn(color, "Color.codigo")));
        Assert.NotNull(await graph.NodeIdAsync(JavaGraphFixture.Qn(JavaTestRepo.DaoPath, "Dao.MAX")));

        var stats = result.Stats;
        Assert.Equal((2, 1, 2), (stats.FieldsMatched, stats.FieldsMatchedToVariable, stats.FieldsCreated));
        Assert.Equal(2, stats.FieldsCreatedWithoutCbmCounterpart);
        Assert.Equal(0, stats.FieldsCreatedNameMismatch);
        Assert.Equal(0, stats.FieldsUnmatchedRatio);
        Assert.False(stats.ExceedsUnmatchedThreshold);
        // Solo los tres campos de Servicio del fixture, que el resultado sintético no declara: los Variable no cuentan.
        Assert.Equal(3, stats.CbmFieldsUnmatched);
        var fields = JsonNode.Parse(result.Outcome.Details!)!["fields"]!;
        Assert.Equal((1, 2), (fields["matched_variable"]!.GetValue<int>(), fields["created_without_cbm_counterpart"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Reads_and_writes_carry_their_lines_and_readwrite_gives_both()
    {
        using var temp = new TempDirectory();
        const string servicio = "com.acme.negocio.Servicio";
        const string ejecutar = "com.acme.negocio.Servicio#ejecutar()";
        var path = JavaTestRepo.ServicioPath;
        var (result, graph) = await ApplyAsync(temp, withReferences: true, extraLines:
        [
            JavaGraphFixture.J(JavaGraphFixture.FieldRef(path, ejecutar, servicio, "com.acme.Bean#id", "write", [11])),
            JavaGraphFixture.J(JavaGraphFixture.FieldRef(path, ejecutar, servicio, "com.acme.Bean#id", "readwrite", [12, 13], count: 3)),
            JavaGraphFixture.J(JavaGraphFixture.FieldRef(path, ejecutar, servicio, "com.acme.Bean#id", "read", [14]))
        ]);
        using var _g = graph;

        var getIdReads = await EdgeAsync(graph, "READS", JavaGraphFixture.GetId, JavaGraphFixture.BeanId);
        Assert.Equal($"[{JavaTestRepo.LineOf(JavaTestRepo.BeanPath, "return id;")}]", getIdReads["lines"]!.ToJsonString());
        Assert.Equal(1, getIdReads["count"]!.GetValue<int>());
        Assert.Equal("[\"JavaAnalyzer\"]", getIdReads["enriched_by"]!.ToJsonString());
        Assert.False(getIdReads.ContainsKey("contexts"));

        var reads = await EdgeAsync(graph, "READS", JavaGraphFixture.Ejecutar, JavaGraphFixture.BeanId);
        Assert.Equal("[12,13,14]", reads["lines"]!.ToJsonString());
        Assert.Equal(4, reads["count"]!.GetValue<int>());
        var writes = await EdgeAsync(graph, "WRITES", JavaGraphFixture.Ejecutar, JavaGraphFixture.BeanId);
        Assert.Equal("[11,12,13]", writes["lines"]!.ToJsonString());
        Assert.Equal(4, writes["count"]!.GetValue<int>());
        Assert.Empty(await EdgesFromAsync(graph, "WRITES", JavaGraphFixture.GetId));

        Assert.Equal((2, 1), (result.Stats.ReadsEdges, result.Stats.WritesEdges));
        var edges = JsonNode.Parse(result.Outcome.Details!)!["edges"]!;
        Assert.Equal((2, 1), (edges["READS"]!.GetValue<int>(), edges["WRITES"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Uses_type_unions_the_contexts_and_can_leave_from_a_method_a_field_or_the_type()
    {
        using var temp = new TempDirectory();
        const string ejecutar = "com.acme.negocio.Servicio#ejecutar()";
        var path = JavaTestRepo.ServicioPath;
        var (result, graph) = await ApplyAsync(temp, withReferences: true, extraLines:
        [
            JavaGraphFixture.J(JavaGraphFixture.TypeRef(path, ejecutar, "com.acme.negocio.Servicio", "com.acme.Bean", ["cast"], [14])),
            // Otro registro con la misma clave (de otro fichero, p. ej.) se funde con el anterior.
            JavaGraphFixture.J(JavaGraphFixture.TypeRef(path, ejecutar, "com.acme.negocio.Servicio", "com.acme.Bean", ["local", "generic"], [15, 16], count: 3)),
            JavaGraphFixture.J(JavaGraphFixture.TypeRef(JavaTestRepo.BeanPath, null, "com.acme.Bean", "com.acme.dao.Dao", ["field"], [7],
                fromField: "com.acme.Bean#id")),
            JavaGraphFixture.J(JavaGraphFixture.TypeRef(JavaTestRepo.BeanPath, null, "com.acme.Bean", "com.acme.dao.Dao", ["annotation"], [6]))
        ]);
        using var _g = graph;

        var bean = await EdgeAsync(graph, "USES_TYPE", JavaGraphFixture.Ejecutar, JavaGraphFixture.BeanQn);
        Assert.Equal("[\"cast\",\"generic\",\"local\"]", bean["contexts"]!.ToJsonString());
        Assert.Equal("[14,15,16]", bean["lines"]!.ToJsonString());
        Assert.Equal(4, bean["count"]!.GetValue<int>());
        Assert.Equal("[\"JavaAnalyzer\"]", bean["enriched_by"]!.ToJsonString());

        var dao = JavaGraphFixture.Qn(JavaTestRepo.DaoPath, "Dao");
        Assert.Equal("[\"local\"]", (await EdgeAsync(graph, "USES_TYPE", JavaGraphFixture.Ejecutar, dao))["contexts"]!.ToJsonString());
        Assert.Equal("[\"field\"]", (await EdgeAsync(graph, "USES_TYPE", JavaGraphFixture.BeanId, dao))["contexts"]!.ToJsonString());
        Assert.Equal("[\"annotation\"]", (await EdgeAsync(graph, "USES_TYPE", JavaGraphFixture.BeanQn, dao))["contexts"]!.ToJsonString());

        // Los cuatro consultar() devuelven Bean: la sobrecarga creada también es origen.
        Assert.Equal("[\"return\"]", (await EdgeAsync(graph, "USES_TYPE", JavaGraphFixture.DaoConsultar + "(Hashtable,long)", JavaGraphFixture.BeanQn))
            ["contexts"]!.ToJsonString());
        Assert.Single(await EdgesFromAsync(graph, "USES_TYPE", JavaGraphFixture.DaoImplConsultar));
        // 4 consultar, 4 desde ejecutar (Dao, DaoImpl, Cache, Bean), 1 desde execute, 1 desde el campo y 1 desde el tipo.
        Assert.Equal(11, result.Stats.UsesTypeEdges);
    }

    [Fact]
    public async Task New_of_an_implicit_constructor_leaves_a_uses_type_with_new_implicit()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp, withReferences: true);
        using var _g = graph;

        var daoImpl = await EdgeAsync(graph, "USES_TYPE", JavaGraphFixture.Ejecutar, JavaGraphFixture.Qn(JavaTestRepo.DaoImplPath, "DaoImpl"));
        Assert.Equal("[\"new_implicit\"]", daoImpl["contexts"]!.ToJsonString());
        Assert.Equal($"[{JavaTestRepo.LineOf(JavaTestRepo.ServicioPath, "new DaoImpl()")}]", daoImpl["lines"]!.ToJsonString());
        Assert.Equal("[\"new_implicit\"]", (await EdgeAsync(graph, "USES_TYPE",
            JavaGraphFixture.Qn(JavaTestRepo.ConsultarActionPath, "ConsultarAction.execute"), JavaGraphFixture.Qn(JavaTestRepo.ServicioPath, "Servicio")))
            ["contexts"]!.ToJsonString());
        // La llamada al constructor implícito sigue sin CALLS, contada aparte.
        Assert.Equal(3, result.Stats.ProjectCallsUnmatchedByReason[JavaGraphWriter.ReasonImplicitConstructor]);
    }

    [Fact]
    public async Task With_schema_2_the_cbm_usage_edges_of_the_analyzed_files_are_replaced()
    {
        using var temp = new TempDirectory();
        const string otro = "src/com/acme/Otro.java";
        var (result, graph) = await ApplyAsync(temp, withReferences: true, arrange: async g =>
        {
            // cbm también deja USAGE desde Module y WRITES heurísticas; y USAGE de un fichero que no se analiza, que se conserva.
            var module = (await g.NodeIdAsync(JavaGraphFixture.Prefix(JavaTestRepo.ServicioPath)))!.Value;
            var bean = (await g.NodeIdAsync(JavaGraphFixture.BeanQn))!.Value;
            var getId = (await g.NodeIdAsync(JavaGraphFixture.GetId))!.Value;
            await g.EdgeAsync(module, bean, "USAGE", "{\"callee\":\"Bean\"}");
            await g.EdgeAsync(getId, bean, "WRITES");
            var outside = await g.NodeAsync("Class", "Otro", JavaGraphFixture.Qn(otro, "Otro"), otro, 1, 3);
            await g.EdgeAsync(outside, bean, "USAGE");
        });
        using var _g = graph;

        Assert.Equal(1, await graph.CountAsync("SELECT COUNT(1) FROM edges WHERE type = 'USAGE'"));
        Assert.Equal(0, await graph.CountAsync("""
            SELECT COUNT(1) FROM edges e JOIN nodes s ON s.id = e.source_id
            WHERE e.type IN ('USAGE', 'USES_TYPE', 'READS', 'WRITES') AND s.file_path <> @otro
              AND NOT (CASE WHEN json_valid(e.properties) THEN EXISTS (SELECT 1 FROM json_each(e.properties, '$.enriched_by') WHERE value = 'JavaAnalyzer') ELSE 0 END)
            """, ("@otro", otro)));
        Assert.Equal(2, result.Stats.CbmEdgesReplacedByType["USAGE"]);
        Assert.Equal(1, result.Stats.CbmEdgesReplacedByType["WRITES"]);
        Assert.Equal(3, result.Stats.CbmEdgesReplacedByType["CALLS"]);
        Assert.Equal(result.Stats.CbmEdgesReplacedByType.Values.Sum(), result.Outcome.CbmEdgesReplaced);
        var replaced = JsonNode.Parse(result.Outcome.Details!)!["cbm_edges_replaced"]!;
        Assert.Equal(2, replaced["USAGE"]!.GetValue<long>());
    }

    [Fact]
    public async Task Targets_inside_anonymous_or_local_classes_get_no_node_and_are_counted()
    {
        using var temp = new TempDirectory();
        const string ejecutar = "com.acme.negocio.Servicio#ejecutar()";
        var path = JavaTestRepo.ServicioPath;
        var (result, graph) = await ApplyAsync(temp, withReferences: true, extraLines:
        [
            JavaGraphFixture.J(JavaGraphFixture.TypeRef(path, ejecutar, "com.acme.negocio.Servicio", "com.acme.negocio.Servicio$1Loc", ["new_implicit"], [15])),
            JavaGraphFixture.J(JavaGraphFixture.FieldRef(path, ejecutar, "com.acme.negocio.Servicio", "com.acme.negocio.Servicio$1#inner", "write", [16, 17])),
            JavaGraphFixture.J(JavaGraphFixture.TypeRef(path, "com.acme.negocio.Servicio#noExiste()", "com.acme.negocio.Servicio", "com.acme.Bean", ["local"], [18]))
        ]);
        using var _g = graph;

        Assert.Equal(0, await graph.CountAsync("SELECT COUNT(1) FROM nodes WHERE qualified_name LIKE '%$%'"));
        Assert.Equal(3, result.Stats.ProjectRefsUnmatchedByReason[JavaGraphWriter.ReasonTargetWithoutNode]);
        Assert.Equal(1, result.Stats.ProjectRefsUnmatchedByReason[JavaGraphWriter.ReasonSourceWithoutNode]);
        Assert.Equal(4, result.Stats.ProjectRefsUnmatched);
        var refs = JsonNode.Parse(result.Outcome.Details!)!["refs"]!;
        Assert.Equal(3, refs["unmatched_by_reason"]![JavaGraphWriter.ReasonTargetWithoutNode]!.GetValue<long>());
        Assert.Equal(4, refs["unmatched"]!.GetValue<long>());
        Assert.Equal(1, refs["unresolved_by_reason"]!["error-type"]!.GetValue<long>());
        Assert.Equal(8, refs["type"]!["project"]!.GetValue<long>());
        // Las llamadas sin nodo siguen en unresolved; las referencias, solo en details.refs.
        Assert.Equal("{\"error-owner\":1,\"implicit_constructor\":3}", result.Outcome.Unresolved);
    }

    [Fact]
    public async Task A_schema_1_result_is_still_written_and_keeps_the_cbm_usage()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        Assert.Equal(1, result.Summary.Schema);
        Assert.Null(result.Summary.Refs);
        Assert.Equal(1, await graph.CountAsync("SELECT COUNT(1) FROM edges WHERE type = 'USAGE'"));
        Assert.Equal(0, await graph.CountAsync("SELECT COUNT(1) FROM edges WHERE type IN ('USES_TYPE', 'READS', 'WRITES')"));
        Assert.False((await PropertiesAsync(graph, JavaGraphFixture.BeanId)).ContainsKey("enriched_by"));
        var details = JsonNode.Parse(result.Outcome.Details!)!;
        Assert.Null(details["refs"]);
        Assert.Equal(0, details["fields"]!["matched"]!.GetValue<int>());
        Assert.False(result.Stats.CbmEdgesReplacedByType.ContainsKey("USAGE"));
        Assert.Equal(EnrichmentRuns.StatusOk, Assert.Single(await EnrichmentRuns.ListAsync(graph.Connection, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_schema_newer_than_the_supported_one_is_rejected()
    {
        using var temp = new TempDirectory();
        var db = Path.Combine(temp.Path, "codebase.db");
        using var graph = await JavaGraphFixture.CreateGraphAsync(db);
        var lines = JavaGraphFixture.ResultLines(withReferences: true);
        lines[^1] = lines[^1].Replace("\"schema\":2", "\"schema\":4", StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => JavaGraphWriter.ApplyAsync(JavaGraphFixture.Request(db),
            JavaAnalysisReader.Read(JavaGraphFixture.WriteResult(temp.Path, lines)), CancellationToken.None));
        Assert.Contains("versión de contrato 4; se esperaba entre 1 y 3", ex.Message);
        Assert.Empty(await EnrichmentRuns.ListAsync(graph.Connection, CancellationToken.None));
    }
}

public class JavaAnalysisReaderTests
{
    [Fact]
    public void Reads_every_record_kind_of_the_contract()
    {
        using var temp = new TempDirectory();
        var records = JavaAnalysisReader.Read(JavaGraphFixture.WriteResult(temp.Path, JavaGraphFixture.ResultLines())).ToList();

        Assert.Equal(JavaTestRepo.SourceFiles.Count, records.OfType<JavaFileRecord>().Count());
        Assert.Equal(7, records.OfType<JavaTypeRecord>().Count());
        Assert.Equal(10, records.OfType<JavaMethodRecord>().Count());
        Assert.Single(records.OfType<JavaDiagnosticRecord>());
        var summary = Assert.IsType<JavaSummaryRecord>(records[^1]);
        Assert.Equal(1, summary.Schema);
        Assert.Equal(7, summary.Calls["project"]);
        Assert.Equal(["javax.servlet.http"], summary.MissingPackages);

        var servicio = records.OfType<JavaTypeRecord>().Single(t => t.Fqn == "com.acme.negocio.Servicio");
        Assert.Equal(new JavaTypeReference { Fqn = "com.acme.negocio.Base", Origin = JavaOrigins.Project }, servicio.Super);
        var overload = records.OfType<JavaMethodRecord>().First(m => m.OwnerFqn == "com.acme.dao.Dao");
        Assert.Equal("com.acme.dao.Dao#consultar(java.util.Hashtable,long)", overload.Key);
        Assert.Equal(["Hashtable", "long"], overload.ParamSimple);
        Assert.True(overload.Abstract);
        var unresolved = records.OfType<JavaCallRecord>().Single(c => c.Origin == JavaOrigins.Unresolved);
        Assert.Null(unresolved.To);
        Assert.Equal("error-owner", unresolved.Reason);
    }

    [Fact]
    public void Reads_the_field_and_ref_records_of_schema_2()
    {
        using var temp = new TempDirectory();
        var records = JavaAnalysisReader.Read(JavaGraphFixture.WriteResult(temp.Path, JavaGraphFixture.ResultLines(withReferences: true))).ToList();

        var field = Assert.Single(records.OfType<JavaFieldRecord>());
        Assert.Equal(("com.acme.Bean", "id", "long", "private"), (field.OwnerFqn, field.Name, field.TypeFqn, field.Access));
        Assert.Equal("com.acme.Bean#id", field.Key);
        Assert.Equal(["Bean"], field.Chain);

        var refs = records.OfType<JavaRefRecord>().ToList();
        Assert.Equal(9, refs.Count);
        var read = Assert.Single(refs, r => r.Kind == JavaRefKinds.Field);
        Assert.Equal(("com.acme.Bean#getId()", "com.acme.Bean#id", JavaRefKinds.Read), (read.From, read.To, read.Access));
        Assert.Empty(read.Contexts);
        var local = Assert.Single(refs, r => r.Contexts.Contains("local"));
        Assert.Null(local.Access);
        Assert.Null(local.FromField);
        Assert.Equal(1, local.Count);
        Assert.Equal([JavaTestRepo.LineOf(JavaTestRepo.ServicioPath, "Dao d =")], local.Lines);

        var summary = Assert.IsType<JavaSummaryRecord>(records[^1]);
        Assert.Equal(2, summary.Schema);
        Assert.Equal(1, summary.Fields);
        Assert.Equal(8, summary.Refs!.Type[JavaOrigins.Project]);
        Assert.Equal(3, summary.Refs.Contexts["new_implicit"]);
        Assert.Equal(9, summary.Refs.Records);
        Assert.Equal(1, summary.Refs.UnresolvedByReason["error-type"]);
        Assert.True(JavaAnalysisReader.IsSupportedSchema(1));
        Assert.True(JavaAnalysisReader.IsSupportedSchema(3));
        Assert.False(JavaAnalysisReader.IsSupportedSchema(4));
    }

    [Fact]
    public void Unknown_kinds_and_blank_lines_are_skipped_and_bad_lines_report_their_number()
    {
        using var temp = new TempDirectory();
        var path = JavaGraphFixture.WriteResult(temp.Path, ["", "{\"k\":\"future\",\"x\":1}", "{\"k\":\"file\",\"path\":\"a/B.java\"}", "{\"k\":\"file\",\"pa"]);

        var ex = Assert.Throws<InvalidDataException>(() => JavaAnalysisReader.Read(path).ToList());
        Assert.Contains(":4:", ex.Message);
        Assert.Single(JavaAnalysisReader.Read(path).Take(1));
        Assert.Null(JavaAnalysisReader.Parse("{\"k\":\"future\"}"));
    }
}

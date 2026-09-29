using System.Text;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// Repositorio Java de prueba pequeño (criterio 10 de la spec), generado en código y sin binarios: sobrecargas, interfaz e
/// implementación, clase anidada, herencia, un fuente en ISO-8859-1 y otro en UTF-8, una raíz de test que debe quedar
/// excluida, un <c>struts-config</c> con forwards globales y locales, definiciones de Tiles y JSP con y sin scriptlets.
/// Lo usan los tests del escritor del grafo, del plugin Struts/JSP y del paso de extremo a extremo.
/// </summary>
public static class JavaTestRepo
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public const string BeanPath = "src/com/acme/Bean.java";
    public const string DaoPath = "src/com/acme/dao/Dao.java";
    public const string DaoImplPath = "src/com/acme/dao/DaoImpl.java";
    public const string BasePath = "src/com/acme/negocio/Base.java";
    public const string ServicioPath = "src/com/acme/negocio/Servicio.java";
    public const string ConsultarActionPath = "src/com/acme/web/ConsultarAction.java";
    public const string ServicioTestPath = "test/com/acme/ServicioTest.java";
    public const string StrutsConfigPath = "WebContent/WEB-INF/struts-config.xml";
    public const string TilesDefsPath = "WebContent/WEB-INF/tiles-defs.xml";
    public const string ConsultaJspPath = "WebContent/pages/consulta.jsp";
    public const string CabeceraJspPath = "WebContent/pages/cabecera.jsp";
    public const string ResultadoJspPath = "WebContent/pages/resultado.jsp";
    public const string LayoutJspPath = "WebContent/pages/layout.jsp";

    /// <summary>Los fuentes Java fuera de la raíz de test, que son los que se analizan.</summary>
    public static IReadOnlyList<string> SourceFiles { get; } =
        [BeanPath, DaoPath, DaoImplPath, BasePath, ServicioPath, ConsultarActionPath];

    /// <summary>Ficheros del repositorio: ruta relativa con <c>/</c>, texto y codificación con la que se escribe.</summary>
    public static IReadOnlyList<(string Path, string Text, Encoding Encoding)> Files { get; } =
    [
        ("pom.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.acme</groupId>
              <artifactId>acme-web</artifactId>
              <version>1.0</version>
              <packaging>war</packaging>
              <properties>
                <project.build.sourceEncoding>ISO-8859-1</project.build.sourceEncoding>
              </properties>
              <build>
                <testSourceDirectory>./test</testSourceDirectory>
              </build>
            </project>

            """, Utf8),

        (BeanPath, """
            package com.acme;

            /**
             * Año de la acción.
             */
            public class Bean {
                private long id;

                public long getId() {
                    return id;
                }
            }

            """, Utf8),

        (DaoPath, """
            package com.acme.dao;

            import com.acme.Bean;

            public interface Dao {
                Bean consultar(java.util.Hashtable c, long id);
                Bean consultar(java.util.Hashtable c);
            }

            """, Utf8),

        (DaoImplPath, """
            package com.acme.dao;

            import com.acme.Bean;

            public class DaoImpl implements Dao {
                public Bean consultar(java.util.Hashtable c, long id) {
                    return null;
                }

                public Bean consultar(java.util.Hashtable c) {
                    return null;
                }

                public static class Cache {
                    public void put(String k) {}
                }
            }

            """, Utf8),

        (BasePath, """
            package com.acme.negocio;

            public abstract class Base {
                protected void log(String m) {}
            }

            """, Utf8),

        (ServicioPath, """
            package com.acme.negocio;

            import com.acme.dao.Dao;
            import com.acme.dao.DaoImpl;

            public class Servicio extends Base {
                /**
                 * Añade la acción.
                 */
                public void ejecutar() {
                    Dao d = new DaoImpl();
                    d.consultar(new java.util.Hashtable(), 1L);
                    d.consultar(new java.util.Hashtable());
                    log("x");
                    new DaoImpl.Cache().put("k");
                    String s = String.valueOf(1);
                    if (s.isEmpty() && s.length() > 2) {
                        s = null;
                    }
                }

                public void web(javax.servlet.http.HttpServletRequest r) {
                    r.getSession();
                }
            }

            """, Latin1),

        (ConsultarActionPath, """
            package com.acme.web;

            public class ConsultarAction {
                public String execute() {
                    return new com.acme.negocio.Servicio().toString();
                }
            }

            """, Utf8),

        (ServicioTestPath, """
            package com.acme;

            public class ServicioTest {
                public void probar() {}
            }

            """, Utf8),

        (StrutsConfigPath, """
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <!DOCTYPE struts-config PUBLIC
              "-//Apache Software Foundation//DTD Struts Configuration 1.1//EN"
              "http://jakarta.apache.org/struts/dtds/struts-config_1_1.dtd">
            <struts-config>
              <form-beans>
                <form-bean name="consultaForm" type="com.acme.web.ConsultaForm"/>
              </form-beans>
              <global-forwards>
                <forward name="inicio" path="/pages/consulta.jsp"/>
              </global-forwards>
              <action-mappings>
                <action path="/consultar" type="com.acme.web.ConsultarAction" name="consultaForm" scope="request" input="/pages/consulta.jsp">
                  <forward name="ok" path="/pages/resultado.jsp"/>
                  <forward name="tile" path="consulta.def"/>
                </action>
              </action-mappings>
            </struts-config>

            """, Latin1),

        (TilesDefsPath, """
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <tiles-definitions>
              <definition name="consulta.def" path="/pages/layout.jsp">
                <put name="body" value="/pages/resultado.jsp"/>
              </definition>
              <definition name="consulta.hija" extends="consulta.def"/>
            </tiles-definitions>

            """, Latin1),

        (ConsultaJspPath, """
            <%@ page import="com.acme.dao.DaoImpl, java.sql.*" %>
            <%@ taglib uri="/WEB-INF/struts-html.tld" prefix="html" %>
            <%@ include file="cabecera.jsp" %>
            <html:form action="/consultar">
              <input type="submit" value="Consultar"/>
            </html:form>
            <%
                String q = "SELECT * FROM T WHERE ID = 1";
                session.getAttribute("u");
            %>
            <p><%= q %></p>
            <%-- SELECT oculto FROM X --%>

            """, Utf8),

        (CabeceraJspPath, """
            <div class="cabecera">Cabecera</div>

            """, Utf8),

        (ResultadoJspPath, """
            <html><body><p>Resultado</p></body></html>

            """, Utf8),

        (LayoutJspPath, """
            <html>
            <body>
            <jsp:include page="/pages/cabecera.jsp"/>
            <div>Cuerpo</div>
            </body>
            </html>

            """, Utf8)
    ];

    /// <summary>Escribe el repositorio en <paramref name="directory"/> (se crea si no existe) y devuelve su ruta completa.</summary>
    public static string WriteTo(string directory)
    {
        var root = Path.GetFullPath(directory);
        foreach (var (path, text, encoding) in Files)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, encoding.GetBytes(text.ReplaceLineEndings("\n")));
        }
        return root;
    }

    /// <summary>Texto de un fichero del repositorio.</summary>
    public static string TextOf(string relativePath) =>
        Files.Single(f => f.Path == relativePath).Text.ReplaceLineEndings("\n");

    /// <summary>Línea (desde 1) de la primera aparición de <paramref name="fragment"/> en el fichero.</summary>
    public static int LineOf(string relativePath, string fragment, int occurrence = 1)
    {
        var lines = TextOf(relativePath).Split('\n');
        var seen = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(fragment, StringComparison.Ordinal) && ++seen == occurrence)
                return i + 1;
        }
        throw new ArgumentException($"'{fragment}' no aparece {occurrence} vez/veces en {relativePath}.");
    }

    /// <summary>Número de líneas del fichero (la última, vacía tras el salto final, no cuenta).</summary>
    public static int LineCount(string relativePath) => TextOf(relativePath).TrimEnd('\n').Split('\n').Length;
}

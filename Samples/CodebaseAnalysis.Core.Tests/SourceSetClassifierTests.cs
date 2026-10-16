using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="SourceSetClassifier"/> (T3): ruta relativa a source set por convención, sin adivinar. Cubre Maven y Gradle,
/// .NET, repositorios mixtos y el caso sin evidencia, que es <c>unknown</c>.
/// </summary>
public sealed class SourceSetClassifierTests
{
    [Theory]
    // Maven y Gradle
    [InlineData("src/main/java/com/acme/billing/Invoice.java", "production")]
    [InlineData("src/main/resources/app.properties", "production")]
    [InlineData("web/src/main/java/com/acme/Order.java", "production")]
    [InlineData("src/test/java/com/acme/billing/InvoiceTest.java", "test")]
    [InlineData("src/test/resources/fixtures.xml", "test")]
    [InlineData("core/src/integrationTest/java/com/acme/FlowIT.java", "test")]
    [InlineData("app/src/androidTest/java/com/acme/UiTest.java", "test")]
    [InlineData("app/src/debug/java/com/acme/DebugMenu.java", "debug")]
    [InlineData("src/qa/java/com/acme/QaHooks.java", "qa")]
    [InlineData("target/generated-sources/annotations/com/acme/Gen.java", "generated")]
    [InlineData("build/generated/source/proto/Msg.java", "generated")]
    // Un paquete que se llama como un source set, dentro de src/main, sigue siendo producción
    [InlineData("src/main/java/com/acme/test/Probe.java", "production")]
    [InlineData("src/main/java/com/acme/debug/Dump.java", "production")]
    [InlineData("src/main/java/com/acme/samples/Fixture.java", "production")]
    // ...salvo lo generado
    [InlineData("src/main/java/com/acme/generated/Dto.java", "generated")]
    // Ant, sin src/main: el código sin marcas es producción
    [InlineData("geo/struts/billing/actions/PayAction.java", "production")]
    [InlineData("src/geo/dao/OrderDao.java", "production")]
    // .NET
    [InlineData("Billing/Invoice.cs", "production")]
    [InlineData("Billing.Tests/InvoiceTests.cs", "test")]
    [InlineData("Billing.Core.UnitTests/Foo.cs", "test")]
    [InlineData("tests/Billing.IntegrationTests/Program.cs", "test")]
    [InlineData("Billing/obj/Debug/net10.0/Billing.AssemblyInfo.cs", "generated")]
    [InlineData("Billing/Models/Invoice.g.cs", "generated")]
    [InlineData("Billing/Forms/Main.Designer.cs", "generated")]
    [InlineData("Billing/Models/Invoice.generated.cs", "generated")]
    [InlineData("Samples/Demo.App/Program.cs", "samples")]
    [InlineData("examples/quickstart/Program.cs", "samples")]
    // Un proyecto de test dentro de samples es test, no samples
    [InlineData("Samples/Demo.App.Tests/ProgramTests.cs", "test")]
    // Mixtas: el nombre del fichero también cuenta cuando no hay carpeta
    [InlineData("billing/InvoiceTest.java", "test")]
    [InlineData("billing/InvoiceTests.cs", "test")]
    [InlineData("billing/TestHelper.java", "test")]
    [InlineData("web/js/invoice.spec.ts", "test")]
    [InlineData("web/js/invoice.test.js", "test")]
    [InlineData("web/js/app.min.js", "generated")]
    [InlineData("web/js/app.js", "production")]
    [InlineData("__tests__/app.js", "test")]
    [InlineData("qa/scripts/Smoke.java", "qa")]
    // Con barras de Windows y mayúsculas
    [InlineData("SRC\\TEST\\Java\\X.java", "test")]
    [InlineData("Billing\\Invoice.cs", "production")]
    // Sin evidencia: no se adivina
    [InlineData("README.md", "unknown")]
    [InlineData("docs/design.md", "unknown")]
    [InlineData("tools/release.java", "unknown")]
    [InlineData("scripts/build.cs", "unknown")]
    [InlineData(".github/scripts/release.js", "unknown")]
    // «Tools» dentro del código de la aplicación es una función del producto, no una carpeta de scripts del repositorio
    [InlineData("LLM/Tools/Base/LlmTool.cs", "production")]
    [InlineData("Core/Scripts/Runner.cs", "production")]
    [InlineData("config/settings.xml", "unknown")]
    [InlineData("LICENSE", "unknown")]
    [InlineData("", "unknown")]
    public void A_path_is_classified_by_convention(string path, string expected) =>
        Assert.Equal(expected, SourceSetClassifier.Classify(path));

    [Fact]
    public void A_configured_test_root_wins_over_the_default_for_everything_under_it()
    {
        string[] roots = ["geo/qa-suite/unit"];

        Assert.Equal("test", SourceSetClassifier.Classify("geo/qa-suite/unit/billing/Foo.java", testRoots: roots));
        Assert.Equal("test", SourceSetClassifier.Classify("GEO/QA-SUITE/UNIT/Foo.java", testRoots: roots));
        // Lo que solo empieza igual no cuelga de la raíz
        Assert.Equal("production", SourceSetClassifier.Classify("geo/qa-suite/unit2/Foo.java", testRoots: roots));
    }

    [Fact]
    public void The_counted_extensions_decide_what_is_code_when_there_is_no_marker()
    {
        Assert.Equal("unknown", SourceSetClassifier.Classify("billing/Foo.py"));
        Assert.Equal("production", SourceSetClassifier.Classify("billing/Foo.py", codeExtensions: [".py"]));
        Assert.Equal("unknown", SourceSetClassifier.Classify("billing/Foo.java", codeExtensions: [".py"]));
        // Una marca explícita clasifica aunque no sea código
        Assert.Equal("test", SourceSetClassifier.Classify("src/test/data/orders.csv", codeExtensions: [".py"]));
    }

    [Fact]
    public void Only_the_documented_source_sets_come_out()
    {
        string[] paths =
        [
            "src/main/A.java", "src/test/A.java", "qa/A.java", "src/debug/A.java", "gen/generated/A.java", "samples/A.java", "docs/A.md", "a/A.java"
        ];

        foreach (var path in paths)
            Assert.Contains(SourceSetClassifier.Classify(path), SourceSets.All);
        Assert.Equal(["production", "test", "qa", "debug", "generated", "samples", "unknown"], SourceSets.All);
    }

    [Fact]
    public void Only_production_counts_as_production()
    {
        Assert.True(SourceSets.IsProduction("production"));
        foreach (var other in SourceSets.All.Where(s => s != "production"))
            Assert.False(SourceSets.IsProduction(other));
    }
}

using System.Text;
using OfficeTools.Extractors;
using Xunit;

namespace OfficeTools.Tests;

public class AccessVbaExtractorTests
{
    [Fact]
    public async Task ParseExportDirectoryAsync_ShouldExtractModulesFormsAndRelationships()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "OfficeTools_VbaTest_" + Guid.NewGuid().ToString("N"));
        var modulesDir = Path.Combine(tempDir, "modules");
        var formsDir = Path.Combine(tempDir, "forms");
        Directory.CreateDirectory(modulesDir);
        Directory.CreateDirectory(formsDir);

        try
        {
            // 1. Create a sample VBA module (UTF-8)
            var moduleCode = @"
Option Compare Database
Option Explicit

Const Password As String = ""Secret123""

' Calculates standard discount
Public Function CalcularDescuento(ByVal precio As Double, ByVal pct As Double) As Double
    Dim resultado As Double
    resultado = precio * (pct / 100)
    Call RegistrarLog(""Descuento calculado"")
    CalcularDescuento = resultado
End Function

Public Sub RegistrarLog(ByVal msg As String)
    Dim sql As String
    sql = ""INSERT INTO TLogs (Mensaje) VALUES ('"" & msg & ""')""
    CurrentDb.Execute sql
End Sub
";
            File.WriteAllText(Path.Combine(modulesDir, "MDescuentos.bas"), moduleCode, Encoding.UTF8);

            // 2. Create a sample Form with UI controls and CodeBehind (UTF-16 LE)
            var formContent = @"Version =21
Begin Form
    Caption =""Formulario de Ventas""
    Begin
        Begin CommandButton
            Name =""btnCalcular""
            Caption =""Calcular""
            OnClick =""[Event Procedure]""
        End
        Begin TextBox
            Name =""txtTotal""
            ControlSource =""TotalVenta""
        End
    End
End
CodeBehindForm
Option Compare Database
Option Explicit

Private Sub btnCalcular_Click()
    Dim d As Double
    d = CalcularDescuento(100, 15)
    Dim rs As Object
    Set rs = CurrentDb.OpenRecordset(""TPedidos"")
End Sub
";
            File.WriteAllText(Path.Combine(formsDir, "FVentas.txt"), formContent, Encoding.Unicode);

            var extractor = new AccessVbaExtractor();

            // Act
            var project = await extractor.ParseExportDirectoryAsync(tempDir, "TestDb.accdb");

            // Assert
            Assert.Single(project.Modules);
            var mod = project.Modules[0];
            Assert.Equal("MDescuentos", mod.Name);
            Assert.Equal(2, mod.Procedures.Count);

            var func = mod.Procedures.First(p => p.Name == "CalcularDescuento");
            Assert.Equal("Function", func.Kind);
            Assert.Equal("Double", func.ReturnType);
            Assert.Contains("RegistrarLog", func.Calls.First());
            Assert.Contains("Calculates standard discount", func.Docstring ?? "");

            var subLog = mod.Procedures.First(p => p.Name == "RegistrarLog");
            Assert.Equal("Sub", subLog.Kind);
            Assert.Contains("TLogs", subLog.ReadsTables); // Detected table reference

            Assert.Single(project.Forms);
            var form = project.Forms[0];
            Assert.Equal("FVentas", form.Name);
            Assert.Equal("Formulario de Ventas", form.Caption);
            Assert.True(form.Controls.Count >= 2);
            Assert.Contains(form.Controls, c => c.Name == "btnCalcular" && c.ControlType == "CommandButton");

            Assert.Single(form.Procedures);
            var formProc = form.Procedures[0];
            Assert.Equal("btnCalcular_Click", formProc.Name);
            Assert.True(formProc.IsEventHandler);
            Assert.Contains("CalcularDescuento", formProc.Calls.First());
            Assert.Contains("TPedidos", formProc.ReadsTables);

            Assert.True(project.TotalLoc > 10);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}

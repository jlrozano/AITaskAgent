namespace OfficeTools.Models;

/// <summary>
/// Represents a VBA procedure (Sub, Function, or Property) within an Access module, form, or report.
/// </summary>
public class AccessVbaProcedure
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "Sub"; // Sub, Function, Property
    public string ParentType { get; set; } = "Module"; // Module, Form, Report
    public string ParentName { get; set; } = string.Empty;
    public string QualifiedName => $"{ParentType}.{ParentName}.{Name}";
    public string ReturnType { get; set; } = "Void";
    public string Args { get; set; } = "()";
    public int StartLine { get; set; } = 1;
    public int EndLine { get; set; } = 1;
    public int Loc => Math.Max(1, EndLine - StartLine + 1);
    public string? Docstring { get; set; }
    public List<string> BodyLines { get; set; } = new();
    public bool IsEventHandler { get; set; }
    public List<string> Calls { get; set; } = new();
    public List<string> ReadsTables { get; set; } = new();
    public List<string> WritesTables { get; set; } = new();
    public List<string> ExecutesQueries { get; set; } = new();
    public List<string> DetectedIssues { get; set; } = new();
}

/// <summary>
/// Represents a control inside an Access Form.
/// </summary>
public class AccessFormControl
{
    public string Name { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty; // CommandButton, TextBox, ComboBox, Subform, etc.
    public string? Caption { get; set; }
    public string? ControlSource { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new();
}

/// <summary>
/// Represents an Access Form with its UI controls and code-behind event handlers.
/// </summary>
public class AccessFormInfo
{
    public string Name { get; set; } = string.Empty;
    public string QualifiedName => $"Form.{Name}";
    public string FilePath { get; set; } = string.Empty;
    public string? Caption { get; set; }
    public List<AccessFormControl> Controls { get; set; } = new();
    public List<AccessVbaProcedure> Procedures { get; set; } = new();
    public int TotalLoc => Procedures.Sum(p => p.Loc);
}

/// <summary>
/// Represents a standard or class VBA Module (.bas, .cls).
/// </summary>
public class AccessModuleInfo
{
    public string Name { get; set; } = string.Empty;
    public string QualifiedName => $"Module.{Name}";
    public string FilePath { get; set; } = string.Empty;
    public bool IsClassModule { get; set; }
    public List<AccessVbaProcedure> Procedures { get; set; } = new();
    public int TotalLoc => Procedures.Sum(p => p.Loc);
}

/// <summary>
/// Represents an Access Report with code-behind.
/// </summary>
public class AccessReportInfo
{
    public string Name { get; set; } = string.Empty;
    public string QualifiedName => $"Report.{Name}";
    public string FilePath { get; set; } = string.Empty;
    public List<AccessVbaProcedure> Procedures { get; set; } = new();
    public int TotalLoc => Procedures.Sum(p => p.Loc);
}

/// <summary>
/// Contains all extracted VBA code, forms, reports, and procedures from an Access database.
/// </summary>
public class AccessVbaProject
{
    public string DatabasePath { get; set; } = string.Empty;
    public string ExportDirectory { get; set; } = string.Empty;
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public List<AccessModuleInfo> Modules { get; set; } = new();
    public List<AccessFormInfo> Forms { get; set; } = new();
    public List<AccessReportInfo> Reports { get; set; } = new();

    public int TotalLoc =>
        Modules.Sum(m => m.TotalLoc) +
        Forms.Sum(f => f.TotalLoc) +
        Reports.Sum(r => r.TotalLoc);

    public IEnumerable<AccessVbaProcedure> AllProcedures =>
        Modules.SelectMany(m => m.Procedures)
            .Concat(Forms.SelectMany(f => f.Procedures))
            .Concat(Reports.SelectMany(r => r.Procedures));
}

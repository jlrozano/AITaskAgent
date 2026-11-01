using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Steps;
using Microsoft.Extensions.DependencyInjection;

namespace AITaskAgent.CSharp.Tests;

/// <summary><see cref="IExtension.Info"/> of <see cref="CSharpExtension"/>: what it declares is what it registers.</summary>
public sealed class CSharpExtensionInfoTests
{
    [Fact]
    public void Info_declares_the_CSharpStep_and_the_registration_matches()
    {
        CSharpExtension extension = new();
        ExtensionCapture capture = new();

        extension.Register(capture);

        Assert.Equal(["CSharpStep"], extension.Info().Of(ExtensionKinds.Step).Select(e => e.Name));
        (IReadOnlyList<ExtensionEntry> missing, IReadOnlyList<ExtensionEntry> extra) = ExtensionInfo.Difference(extension.Info(), capture.ActualInfo(extension));
        Assert.Empty(missing);
        Assert.Empty(extra);
    }
}

using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Steps;
using AITaskAgent.Core.StepResults;

namespace AITaskAgent.Tests;

/// <summary>
/// CreateSchemaForType/CreateAdditionalPropertiesSchema recursed into self-referencing class
/// graphs (e.g. a node with a nullable reference to its own type) without any cycle guard,
/// producing a StackOverflowException that kills the process. GetStepActivatorInfo now threads
/// a HashSet of in-progress types through the recursion and stops expanding a type it is
/// already expanding, leaving it as a bare Object instead of recursing forever.
/// </summary>
public class StepResultFactorySchemaTests
{
    private sealed class SelfRefType
    {
        public string Name { get; set; } = "";
        public SelfRefType? Child { get; set; }
    }

    private sealed class SelfRefStepResult(IStep step, SelfRefType? value = default)
        : StepResult<SelfRefType>(step, value);

    [Fact]
    public void Self_referencing_type_does_not_overflow_and_yields_a_schema()
    {
        var step = new EmptyStep("Analyzer");

        StepResultFactory.StepActivatorInfo info =
            StepResultFactory.GetStepActivatorInfo(typeof(SelfRefStepResult));

        Assert.NotNull(info.JsonSchema);

        // Sanity: the factory can still build an instance carrying a self-referencing value.
        var value = new SelfRefType { Name = "root", Child = new SelfRefType { Name = "child" } };
        object instance = info.CreateInstance(step, value);

        Assert.IsType<SelfRefStepResult>(instance);
    }
}

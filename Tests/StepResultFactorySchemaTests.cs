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

    private sealed class AnnotatedType
    {
        [System.ComponentModel.DataAnnotations.Range(1, 10)]
        public int Count { get; set; }

        [System.ComponentModel.DataAnnotations.Range(0.5, 2.5)]
        public double Ratio { get; set; }

        [System.ComponentModel.DataAnnotations.Required]
        public string? Title { get; set; }

        public string? Note { get; set; }
    }

    private sealed class AnnotatedStepResult(IStep step, AnnotatedType? value = default)
        : StepResult<AnnotatedType>(step, value);

    [Fact]
    public void Range_applies_to_integer_and_number_and_Required_marks_the_property()
    {
        var schema = StepResultFactory.GetStepActivatorInfo(typeof(AnnotatedStepResult)).JsonSchema!;

        Assert.Equal(1m, schema.Properties["count"].Minimum);
        Assert.Equal(10m, schema.Properties["count"].Maximum);
        Assert.Equal(0.5m, schema.Properties["ratio"].Minimum);
        Assert.Contains("title", schema.RequiredProperties);
        Assert.DoesNotContain("note", schema.RequiredProperties);
    }

    [Fact]
    public void GetParameters_skips_the_indexer_of_ParallelResult()
    {
        var result = new ParallelResult(new EmptyStep("P"));

        var parameters = result.GetParameters();

        Assert.DoesNotContain("Item", parameters.Keys);
    }
}

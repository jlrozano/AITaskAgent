using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Support.Template;

namespace Samples.AdvancedLlmPipeline;

/// <summary>
/// Example of how to refactor StoryPipelineFactory to use JsonTemplateEngine.
/// This demonstrates token-efficient prompt building for LLM steps.
/// </summary>
internal static class TemplateUsageExample
{
    /// <summary>
    /// Example: Refactoring the Critic step to use templates instead of manual string concatenation.
    /// </summary>
    public static string BuildCriticPromptWithTemplate(PipelineContext context)
    {
        var templateEngine = new JsonTemplateEngine();

        // Get results from previous parallel writers
        var results = (context.StepResults["Router/NewStoryPhase/ParallelWriters"] as ParallelResult)?.Value;

        // Template with default values for missing drafts
        var template = @"Analyze the following two drafts of the same story.

Draft 1:
{{Writer1.Content ?? No draft 1}}

Draft 2:
{{Writer2.Content ?? No draft 2}}";

        return JsonTemplateEngine.Render(template, results ?? new Dictionary<string, IStepResult>());
    }

    /// <summary>
    /// Example: Using CSV formatter for database results (token-efficient).
    /// </summary>
    public static string BuildDatabaseAnalysisPrompt(object dbResults)
    {
        var templateEngine = new JsonTemplateEngine();

        var template = @"Analyze the following customer data and identify trends:

{{Customers:csv}}

Focus on:
1. Age distribution
2. Geographic patterns
3. Purchase behavior";

        return JsonTemplateEngine.Render(template, dbResults);
    }

    /// <summary>
    /// Example: Using JSON formatter for structured configuration.
    /// </summary>
    public static string BuildConfigurationPrompt(object config)
    {
        var templateEngine = new JsonTemplateEngine();

        var template = @"Review the following system configuration and suggest optimizations:

{{Config:json:indent}}

Consider performance, security, and scalability.";

        return JsonTemplateEngine.Render(template, config);
    }

    /// <summary>
    /// Example: Complex prompt with nested properties and formatting.
    /// </summary>
    public static string BuildComplexPrompt(object analysisResult)
    {
        var templateEngine = new JsonTemplateEngine();

        var template = @"Story Analysis Report
Generated: {{Timestamp:yyyy-MM-dd HH:mm}}

Author: {{Author.Name}} ({{Author.Email}})
Story Title: {{Story.Title}}
Word Count: {{Story.WordCount:N0}}

Quality Metrics:
- Grammar Score: {{Metrics.Grammar:F2}}%
- Readability: {{Metrics.Readability:F2}}%
- Engagement: {{Metrics.Engagement:F2}}%

Recommendations:
{{Recommendations:json:indent}}

Previous Versions:
{{PreviousVersions:csv}}";

        return JsonTemplateEngine.Render(template, analysisResult);
    }

    /// <summary>
    /// Example data structure for database results.
    /// </summary>
    public record DatabaseQueryResult(
        Customer[] Customers
    );

    public record Customer(
        string Name,
        int Age,
        string City,
        decimal TotalPurchases
    );

    /// <summary>
    /// Example: How this would look with actual data.
    /// </summary>
    public static void DemonstrateTokenSavings()
    {
        var templateEngine = new JsonTemplateEngine();

        var data = new DatabaseQueryResult(
            Customers: [
                new("John Doe", 30, "NYC", 1250.50m),
                new("Jane Smith", 25, "LA", 890.25m),
                new("Bob Johnson", 35, "Chicago", 2100.75m)
            ]
        );

        // CSV format (token-efficient)
        var csvPrompt = JsonTemplateEngine.Render("{{Customers:csv}}", data);
        Console.WriteLine("CSV Format (Token-Efficient):");
        Console.WriteLine(csvPrompt);
        Console.WriteLine($"Approximate tokens: ~{EstimateTokens(csvPrompt)}");
        Console.WriteLine();

        // JSON format (preserves structure)
        var jsonPrompt = JsonTemplateEngine.Render("{{Customers:json}}", data);
        Console.WriteLine("JSON Format:");
        Console.WriteLine(jsonPrompt);
        Console.WriteLine($"Approximate tokens: ~{EstimateTokens(jsonPrompt)}");
        Console.WriteLine();

        // Token savings
        var savings = ((EstimateTokens(jsonPrompt) - EstimateTokens(csvPrompt)) / (double)EstimateTokens(jsonPrompt)) * 100;
        Console.WriteLine($"Token savings with CSV: {savings:F1}%");
    }

    private static int EstimateTokens(string text) => text.Length / 4; // Rough estimate
}

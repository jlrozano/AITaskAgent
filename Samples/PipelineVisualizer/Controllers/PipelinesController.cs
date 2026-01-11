using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using PipelineVisualizer.Services;

namespace PipelineVisualizer.Controllers;

/// <summary>
/// API endpoints for pipeline management and schema generation.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class PipelinesController(
    PipelineExecutor pipelineExecutor,
    PipelineSchemaGenerator schemaGenerator) : ControllerBase
{
    /// <summary>
    /// Gets the list of available pipelines.
    /// </summary>
    [HttpGet]
    public IActionResult GetPipelines()
    {
        var pipelines = new[]
        {
            new
            {
                name = "StoryMachine",
                description = "Adaptive story creation pipeline with memory and multi-model collaboration",
                version = "1.0"
            }
        };

        return Ok(new { pipelines });
    }

    /// <summary>
    /// Gets the JSON schema for a specific pipeline.
    /// </summary>
    /// <param name="name">Pipeline name (e.g., "StoryMachine")</param>
    [HttpGet("{name}/schema")]
    public IActionResult GetPipelineSchema(string name)
    {
        try
        {
            var pipeline = pipelineExecutor.GetPipeline(name);
            var schema = schemaGenerator.GeneratePipelineSchema(
                name,
                "Adaptive story creation pipeline with memory and multi-model collaboration",
                "1.0",
                pipeline);

            return Ok(schema);
        }
        catch (ArgumentException)
        {
            return NotFound(new { error = $"Pipeline '{name}' not found" });
        }
    }

    /// <summary>
    /// Executes the pipeline for the given request.
    /// </summary>
    [HttpPost("execute")]
    public async Task<IActionResult> ExecutePipeline(
        [FromBody] Models.ChatRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new Models.ChatResponse
            {
                CorrelationId = Guid.NewGuid().ToString(),
                ConversationId = request.ConversationId ?? Guid.NewGuid().ToString(),
                Error = "Message cannot be empty"
            });
        }

        var response = await pipelineExecutor.ExecuteAsync(request, cancellationToken);
        return response.Success ? Ok(response) : UnprocessableEntity(response);
    }
}

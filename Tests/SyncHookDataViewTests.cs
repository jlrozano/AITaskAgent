using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Observability.Events;
using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace AITaskAgent.Tests;

/// <summary>
/// The data views of the synchronous slots (2026-10-24, architecture §18.8): <c>enrichActivityBefore/After</c> (activity ids read-only, display
/// name and tags written back to the real <see cref="Activity"/>), <c>enrichStarted/Completed</c> (events rebuilt with <c>with</c>; an event type the
/// framework cannot rebuild refuses the write) and <c>configureRequest</c> (each write a new request; the original is never touched).
/// </summary>
public sealed class SyncHookDataViewTests
{
    private static readonly StepBase Step = new HookProbeStep();

    private static IStepResult Value(JToken value) => new AITaskAgent.Core.StepResults.JsonStepResult(Step, value);

    private sealed class ActivityScope : IDisposable
    {
        private readonly ActivitySource _source = new("hook-tests-" + Guid.NewGuid().ToString("N"));
        private readonly ActivityListener _listener;

        public ActivityScope()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s == _source,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(_listener);
            Activity = _source.StartActivity("step.run")!;
        }

        public Activity Activity { get; }

        public void Dispose()
        {
            Activity.Dispose();
            _listener.Dispose();
            _source.Dispose();
        }
    }

    [Fact]
    public void The_activity_view_reads_ids_and_tags_and_writes_display_name_and_tags_back()
    {
        using ActivityScope scope = new();
        Activity activity = scope.Activity;
        activity.SetTag("kept", 1.5m);
        activity.SetTag("changed", "old");
        activity.SetTag("removed", true);
        EnrichActivityArgs args = new(Step, activity, Value(new JObject { ["n"] = 1 }), null, new PipelineContext());

        JObject data = HookData.Read(args);
        HookData.Write(args, "tags", new JObject { ["kept"] = 1.5, ["changed"] = "new", ["added"] = new JArray(1, 2) });
        HookData.Write(args, "displayName", "renamed");

        Assert.Equal(activity.TraceId.ToHexString(), data["activity"]!["traceId"]!.Value<string>());
        Assert.Equal("step.run", data["activity"]!["operationName"]!.Value<string>());
        Assert.Equal(1, data["input"]!["n"]!.Value<int>());
        Assert.Equal(JTokenType.Null, data["result"]!.Type);
        Dictionary<string, object?> tags = activity.TagObjects.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(1.5m, tags["kept"]);                 // unchanged JSON: the original object (a decimal) stays
        Assert.Equal("new", tags["changed"]);
        Assert.Equal("[1,2]", tags["added"]);             // arrays and objects become their JSON text
        Assert.False(tags.ContainsKey("removed"));
        Assert.Equal("renamed", activity.DisplayName);
        Assert.Throws<InvalidOperationException>(() => HookData.Write(args, "activity", new JObject()));
    }

    [Fact]
    public void Without_an_activity_the_view_is_empty_and_writes_are_ignored()
    {
        EnrichActivityArgs args = new(Step, null, null, Value(new JValue(3)), new PipelineContext());

        JObject data = HookData.Read(args);
        HookData.Write(args, "tags", new JObject { ["x"] = 1 });
        HookData.Write(args, "displayName", "x");

        Assert.Equal(JTokenType.Null, data["activity"]!.Type);
        Assert.Empty((JObject)data["tags"]!);
        Assert.Equal(3, data["result"]!.Value<int>());
    }

    [Fact]
    public void The_started_event_is_rebuilt_with_the_written_data_and_keeps_untouched_entries()
    {
        object cost = 0.25m;
        StepStartedEvent started = new() { StepName = "S", StepPath = "p/S", AttemptNumber = 2, AdditionalData = new() { ["cost"] = cost, ["drop"] = "x" } };
        EnrichStartedArgs args = new(Step, started, Value(new JObject()), new PipelineContext());
        IHookOutcome<StepStartedEvent> outcome = args;

        JObject data = HookData.Read(args);
        HookData.Write(args, "additionalData", new JObject { ["cost"] = 0.25, ["drop"] = null, ["model"] = "m" });
        HookData.Write(args, "suppressFromUser", true);

        Assert.Equal(2, data["event"]!["attemptNumber"]!.Value<int>());
        Assert.Equal("p/S", data["event"]!["stepPath"]!.Value<string>());
        StepStartedEvent rebuilt = outcome.Outcome;
        Assert.Same(cost, rebuilt.AdditionalData!["cost"]);
        Assert.Equal("m", rebuilt.AdditionalData["model"]);
        Assert.False(rebuilt.AdditionalData.ContainsKey("drop"));
        Assert.True(rebuilt.SuppressFromUser);
        Assert.Equal(started.Timestamp, rebuilt.Timestamp);
        Assert.Equal(2, rebuilt.AttemptNumber);
        Assert.False(started.SuppressFromUser);               // the base event is not touched
        Assert.Throws<InvalidOperationException>(() => HookData.Write(args, "suppressFromUser", "yes"));
        Assert.Throws<InvalidOperationException>(() => HookData.Write(args, "event", new JObject()));
    }

    private sealed record ForeignCompleted : IStepCompletedEvent
    {
        public string StepName { get; init; } = "S";
        public string EventType => "custom";
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
        public string? CorrelationId { get; init; }
        public bool SuppressFromUser { get; init; }
        public string SchemaVersion { get; init; } = "1.0";
        public TimeSpan Duration { get; init; }
        public string? ErrorMessage { get; init; }
        public IStepResult? StepResult { get; init; }
        public void WriteLog(Microsoft.Extensions.Logging.ILogger logger, Microsoft.Extensions.Logging.LogLevel level) { }
    }

    [Fact]
    public void The_completed_event_is_rebuilt_when_it_is_the_frameworks_and_refused_otherwise()
    {
        StepCompletedEvent completed = new() { StepName = "S", Success = true, Duration = TimeSpan.FromMilliseconds(40), AdditionalData = { ["tokens"] = 12 } };
        EnrichCompletedArgs args = new(Step, completed, Value(new JValue("done")), new PipelineContext());

        JObject data = HookData.Read(args);
        HookData.Write(args, "additionalData", new JObject { ["tokens"] = 12, ["note"] = "x" });

        Assert.True(data["event"]!["success"]!.Value<bool>());
        Assert.Equal(40, data["event"]!["durationMs"]!.Value<double>());
        Assert.Equal("done", data["result"]!.Value<string>());
        StepCompletedEvent rebuilt = Assert.IsType<StepCompletedEvent>(((IHookOutcome<IStepCompletedEvent>)args).Outcome);
        Assert.Equal(12, rebuilt.AdditionalData["tokens"]);
        Assert.Equal("x", rebuilt.AdditionalData["note"]);
        Assert.Equal(TimeSpan.FromMilliseconds(40), rebuilt.Duration);

        EnrichCompletedArgs foreign = new(Step, new ForeignCompleted(), Value(new JValue(1)), new PipelineContext());
        Assert.Equal(JTokenType.Null, HookData.Read(foreign)["event"]!["success"]!.Type);
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => HookData.Write(foreign, "suppressFromUser", true));
        Assert.Contains("ForeignCompleted", refused.Message, StringComparison.Ordinal);
    }

    private static LlmRequest Request()
    {
        ConversationContext conversation = new();
        conversation.AddUserMessage("hello");
        return new LlmRequest
        {
            Conversation = conversation,
            Model = "m1",
            SystemPrompt = "sys",
            Temperature = 0.5f,
            Tools = [new ToolDefinition { Name = "list_dir", Description = "d", ParametersJsonSchema = "{}" }],
            Options = new JObject { ["vendor"] = "x" },
        };
    }

    [Fact]
    public void The_request_view_reads_the_request_as_data_and_each_write_builds_a_new_request()
    {
        LlmRequest original = Request();
        ConfigureRequestArgs args = new(new AITaskAgent.YAML.Steps.YamlLlmStep(), original, new PipelineContext());
        IHookOutcome<LlmRequest> outcome = args;

        JObject data = HookData.Read(args);
        HookData.Write(args, "options", new JObject { ["Temperature"] = 0.5, ["tool_choice"] = "required" });
        HookData.Write(args, "model", "m2");
        HookData.Write(args, "temperature", 0.1);
        HookData.Write(args, "maxTokens", 300);
        HookData.Write(args, "systemPrompt", JValue.CreateNull());

        Assert.Equal("""[{"role":"user","content":"hello"}]""", data["messages"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("""["list_dir"]""", data["tools"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("x", data["options"]!["vendor"]!.Value<string>());
        LlmRequest sent = outcome.Outcome;
        Assert.Equal("m2", sent.Model);
        Assert.Equal(0.1f, sent.Temperature);
        Assert.Equal(300, sent.MaxTokens);
        Assert.Null(sent.SystemPrompt);
        Assert.Equal("required", sent.GetOption<string>("tool_choice"));
        Assert.Null(sent.GetOption<string>("vendor"));
        Assert.Same(original.Conversation, sent.Conversation);
        // The original request is untouched.
        Assert.Equal("m1", original.Model);
        Assert.Equal("x", original.GetOption<string>("vendor"));
        Assert.Null(original.GetOption<string>("tool_choice"));
        Assert.Throws<InvalidOperationException>(() => HookData.Write(args, "model", ""));
        Assert.Throws<InvalidOperationException>(() => HookData.Write(args, "temperature", "hot"));
        Assert.Throws<InvalidOperationException>(() => HookData.Write(args, "options", new JArray()));
        Assert.Throws<InvalidOperationException>(() => HookData.Write(args, "messages", new JArray()));
    }
}

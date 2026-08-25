# Working with open-weight models

Models served through OpenAI-compatible endpoints — GLM, Nemotron, Llama, Qwen, DeepSeek and the
rest, whether on OpenRouter, Groq or a local Ollama — frequently ignore the `tool_calls` protocol.
Asked to call a tool, they write the call into the message text instead, each family in its own
notation. And several emit their chain of thought inline, wrapped in `<think>` tags.

Handled naively, both look like the model failing: no tool is invoked, and the reasoning ends up
pasted into the user-visible answer. The framework normalises both, so a step written against the
standard contract works with these models unchanged.

## Recovering tool calls from the message text

`ToolCallXmlParser` recognises five notations:

| Notation | Shape |
|---|---|
| GLM pipe-delimited | `<\|get_weather><arg_key>city<arg_value>Madrid<\|/get_weather\|>` |
| Named wrapper | `<tool_call>get_weather\n<city>Madrid</city></tool_call>` |
| Plain XML | `<get_weather><city>Madrid</city></get_weather>`, attributes and child elements alike |
| JSON in a wrapper | `<tool_call>{"name":"get_weather","arguments":{...}}</tool_call>` |
| Bare JSON | `{"name":...,"arguments":...}` in the content, object or array |

Names are matched across `-` and `_`, so `<get-weather>` finds `get_weather`.

Matching is deliberately exact, never by substring: `<file>` does **not** resolve to `view_file`.
Prose containing angle brackets would otherwise turn into phantom tool calls, which is a far worse
failure than missing one.

Only names in the tool set the step actually offers are considered, so a tag naming an unknown tool
stays as text.

## Separating reasoning from content

`ThinkTagReasoningExtractor` splits `<think>…</think>` out of the message into
`LlmResponse.Reasoning`, leaving `Content` with the answer alone.

It runs as a character-level state machine rather than a regex over the finished message, because in
streaming the tag arrives split across SSE chunks — `<th` in one, `ink>` in the next. A regex would
miss it and leak the opening tag into the visible output. Each candidate keeps its own parsing
state, so `n > 1` responses do not corrupt each other.

Reasoning is emitted as it arrives, through `LlmResponseEvent` with `IsThinking = true`, so a UI can
show the model thinking without mixing it into the answer.

## Buffering tool calls during streaming

A model that emits a tool call as text emits it token by token. Streaming it straight through would
show the user a half-written JSON object before anyone realises it was a tool call.

So when nothing has been emitted yet and the first content looks like an embedded tool call, the
provider buffers it per candidate instead of forwarding it. On `finish_reason` the buffer is parsed:
if it really was a tool call, synthetic `ToolCallUpdate`s are produced; if not, the text is released
unchanged. Nothing is lost either way.

## Extending it

Both stages are pluggable through `OpenAiPluginRegistry`:

```csharp
OpenAiPluginRegistry.RegisterExtractor(new MyReasoningExtractor());   // IReasoningExtractor
OpenAiPluginRegistry.RegisterNormalizer(new MyToolCallNormalizer());  // IResponseNormalizer
```

- `IReasoningExtractor` works during streaming: `ProcessChunk` per delta, `Flush` at the end, and
  `NormalizeAssembled` for the non-streaming path. Extractors chain — one's output content is the
  next one's input.
- `IResponseNormalizer` runs once the response is assembled, and receives the available tool names.

Two are registered out of the box: `ThinkTagReasoningExtractor` and `XmlToolCallNormalizer`.

The registry is static and global, so registration affects every provider instance in the process
and there is no way to unregister.

## Configuration that helps

```jsonc
"Providers": {
  "OpenRouter": { "ServiceType": "openai", "BaseUrl": "https://openrouter.ai/api/v1/", "ApiKey": "${OPENROUTER_API_KEY}" },
  "Groq":       { "ServiceType": "openai", "BaseUrl": "https://api.groq.com/openai/v1", "ApiKey": "${GROQ_API_KEY}" },
  "Ollama":     { "ServiceType": "openai", "BaseUrl": "http://localhost:11434/v1",      "ApiKey": "ollama" }
},
"Roles": {
  "thinking": {
    "Provider": "OpenRouter",
    "Model": "minimax/minimax-m2.5:free",
    "Inference": { "Temperature": 0.7, "Options": { "enable_thinking": true } }
  }
}
```

Anything a particular vendor understands goes in `Options` and is passed through to the request
verbatim — no framework change needed per parameter.

Two details the OpenAI provider handles for these endpoints: it sends
`stream_options: { include_usage: true }`, without which Ollama and several compatible servers never
report token usage while streaming; and it warns when a response comes back empty with
`completion_tokens <= 1` and a large prompt, which almost always means the prompt filled the context
window and was truncated at the front.

## Repetition

Models that are struggling tend to call the same tool with the same arguments over and over. The
tool-calling loop compares each round against the previous one and stops when nothing is changing,
telling the model why so it can answer with what it has.

Read-only tools get more rope than mutating ones — re-reading a file can be legitimate exploration,
repeating a write cannot — which is why a tool that only reads should say so:

```csharp
public override bool IsReadOnly => true;
```

## See also

- [Configuration](configuration.md) — providers, roles and fallbacks
- [Tools](tools.md) — defining tools
- `Samples/SimpleChatDemo` — four providers, vendor options and a fallback chain

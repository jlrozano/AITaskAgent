global using Xunit;
// Namespace GeminiLlmService and class GeminiLlmService share a name: from inside
// GeminiLlmService.Tests, "GeminiLlmService.GeminiLlmService" resolves the class first and then
// fails looking for a nested type inside it (CS0426). Alias it instead of qualifying every use.
global using GeminiLlmServiceClass = GeminiLlmService.GeminiLlmService;

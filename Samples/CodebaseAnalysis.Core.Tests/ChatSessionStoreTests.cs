using CodebaseAnalysis.Core.Chat;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Tests;

public sealed class ChatSessionStoreTests
{
    [Fact]
    public void Create_get_list_and_delete()
    {
        using var dir = new TempDirectory();
        var store = new ChatSessionStore(Path.Combine(dir.Path, "chat"));

        var a = store.Create("C:/repo", "Primera");
        var b = store.Create("C:/repo");

        Assert.Matches(new Regex("^\\d{8}-\\d{6}-[a-z0-9]{4}$"), a.Id);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal("Primera", store.TryGet(a.Id)!.Title);
        Assert.Null(store.TryGet(b.Id)!.Title);
        Assert.Equal(2, store.List().Count);

        Assert.True(store.Delete(a.Id));
        Assert.False(store.Delete(a.Id));
        Assert.Null(store.TryGet(a.Id));
        Assert.Equal([b.Id], store.List().Select(s => s.Id));
    }

    [Fact]
    public void Save_round_trips_the_documented_format()
    {
        using var dir = new TempDirectory();
        var store = new ChatSessionStore(dir.Path);
        var session = store.Create("C:\\repos\\miapp");
        session.Title = "¿Dónde se valida?";
        session.Turns.Add(new ChatTurn
        {
            Id = "t-0001",
            Question = "¿Dónde se valida?",
            Answer = "En `src/x.cs:12`",
            Status = ChatTurnStatus.Completed,
            Tokens = 10,
            CostUsd = 0.01m,
            FinishReason = "Stop",
            ToolCalls = [new ChatToolCall { Name = "view_file", Arguments = "{\"path\":\"src/x.cs\"}", Success = true, DurationMs = 12 }]
        });
        store.Save(session);

        var raw = File.ReadAllText(Path.Combine(dir.Path, session.Id + ".json"));
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        var turn = doc.RootElement.GetProperty("turns")[0];
        Assert.Equal("completed", turn.GetProperty("status").GetString());
        Assert.Equal("view_file", turn.GetProperty("toolCalls")[0].GetProperty("name").GetString());
        Assert.Contains("¿Dónde se valida?", raw);

        var loaded = store.TryGet(session.Id)!;
        Assert.Equal("En `src/x.cs:12`", Assert.Single(loaded.Turns).Answer);
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temporary_files()
    {
        using var dir = new TempDirectory();
        var store = new ChatSessionStore(dir.Path);
        var session = store.Create("C:/repo");

        for (var i = 0; i < 5; i++)
        {
            session.Turns.Add(new ChatTurn { Id = $"t-{i + 1:D4}", Question = $"q{i}", Answer = "a" });
            store.Save(session);
        }

        Assert.Equal([session.Id + ".json"], Directory.GetFiles(dir.Path).Select(Path.GetFileName));
        Assert.Equal(5, store.TryGet(session.Id)!.Turns.Count);
    }

    [Theory]
    [InlineData("../secreto")]
    [InlineData("..\\secreto")]
    [InlineData("ABC")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("x/y")]
    public void Invalid_ids_are_rejected_without_touching_the_disk(string? id)
    {
        using var dir = new TempDirectory();
        var store = new ChatSessionStore(Path.Combine(dir.Path, "chat"));
        File.WriteAllText(Path.Combine(dir.Path, "secreto.json"), "{}");

        Assert.False(ChatSessionStore.IsValidId(id));
        Assert.Null(store.GetFilePath(id));
        Assert.Null(store.TryGet(id));
        Assert.False(store.Delete(id));
        Assert.True(File.Exists(Path.Combine(dir.Path, "secreto.json")));
    }

    [Fact]
    public void History_uses_only_completed_turns_and_the_last_n()
    {
        var session = new ChatSession
        {
            Turns =
            [
                new ChatTurn { Question = "q1", Answer = "a1", Status = ChatTurnStatus.Completed },
                new ChatTurn { Question = "q2", Status = ChatTurnStatus.Failed, Error = "x" },
                new ChatTurn { Question = "q3", Answer = "parcial", Status = ChatTurnStatus.Cancelled },
                new ChatTurn { Question = "q4", Answer = "a4", Status = ChatTurnStatus.Completed },
                new ChatTurn { Question = "q5", Answer = "a5", Status = ChatTurnStatus.Completed }
            ]
        };

        Assert.Equal([("q1", "a1"), ("q4", "a4"), ("q5", "a5")], ChatSessionStore.BuildHistory(session, 20));
        Assert.Equal([("q4", "a4"), ("q5", "a5")], ChatSessionStore.BuildHistory(session, 2));
        Assert.Empty(ChatSessionStore.BuildHistory(session, 0));
    }

    [Fact]
    public void Default_title_is_the_first_80_characters_on_one_line()
    {
        var question = "línea uno\r\nlínea dos " + new string('x', 100);

        var title = ChatSessionStore.DefaultTitle(question);

        Assert.Equal(80, title.Length);
        Assert.StartsWith("línea uno línea dos", title);
    }

    [Fact]
    public void Unreadable_files_are_skipped_in_the_listing()
    {
        using var dir = new TempDirectory();
        var store = new ChatSessionStore(dir.Path);
        var ok = store.Create("C:/repo");
        File.WriteAllText(Path.Combine(dir.Path, "20260101-000000-zzzz.json"), "{ no es json");

        Assert.Equal([ok.Id], store.List().Select(s => s.Id));
    }
}

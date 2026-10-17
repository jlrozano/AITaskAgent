using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.SharedPass;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// D1 sobre la corrida real de <c>Framework</c> (<c>m\fw</c>): el mismo tamaño que el experimento de la pasada compartida
/// (fixtures Fixtures/shared-pass-2026-10-16: 11 lotes, 177 ficheros, ~202 k tokens) y un lote más (12, ver la aserción). Opcional: sin la BD o sin
/// el mapa de dominios de esa máquina no comprueba nada. Solo lee.
/// </summary>
public sealed class SharedPassFrameworkTests(ITestOutputHelper output)
{
    private const string Root = @"C:\Users\juan.rozano\m\fw";
    private const int ExperimentTokens = 201_848;

    [Fact]
    public async Task Framework_packets_match_the_experiment()
    {
        var db = Path.Combine(Root, ".codebase", "codebase.db");
        var map = File.Exists(db) ? PreparationArtifacts.TryRead<DomainMap>(Path.Combine(Root, "reports"), out _) : null;
        if (map == null)
        {
            output.WriteLine($"Sin {db} o sin su _domain_map.json: no se comprueba.");
            return;
        }

        var set = await PacketBuilder.BuildAllAsync(db, Root, map, new SharedPassOptions(), CancellationToken.None);

        foreach (var (batch, packet) in set.Plan.Batches.Zip(set.Packets))
            output.WriteLine($"{batch.Order,2} L{batch.Level} {batch.Id,-22} files={batch.Files.Count,3} est={batch.EstimatedTokens,6} packet={packet.EstimatedTokens,6}");
        output.WriteLine($"TOTAL packet ~{set.TotalEstimatedTokens} (experimento ~{ExperimentTokens}); avisos: {set.Warnings.Count}");
        foreach (var warning in set.Warnings)
            output.WriteLine(warning);

        // 12 y no los 11 del experimento: desde que MaxBatchTokens mide el paquete entero (cabecera y relaciones, decisión del
        // 2026-10-16), llm-configuration, que con sus relaciones pasa de 50 k, se parte en dos (~44 k y ~7 k). El total apenas cambia.
        Assert.Equal(12, set.Plan.Batches.Count);
        Assert.All(set.Packets, p => Assert.True(p.EstimatedTokens <= new SharedPassOptions().MaxBatchTokens, $"{p.BatchId}: {p.EstimatedTokens}"));
        Assert.Equal(177, set.Plan.Batches.Sum(b => b.Files.Count));
        Assert.Equal("shared", set.Plan.Batches[0].Id);
        Assert.InRange(set.TotalEstimatedTokens, ExperimentTokens * 0.8, ExperimentTokens * 1.2);
    }
}

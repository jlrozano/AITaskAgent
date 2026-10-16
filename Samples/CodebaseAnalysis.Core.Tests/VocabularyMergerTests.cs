using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="NameTokenizer"/>, <see cref="VocabularyIndex"/> y <see cref="VocabularyMerger"/> (T9, criterio A2): la fusión por
/// vocabulario reúne una capacidad cuyas clases cuelgan de ramas distantes del árbol, sin fundir dos capacidades solo porque
/// comparten una carpeta padre o un término genérico. Los nombres de los tests son sintéticos y no pertenecen a ningún repositorio real.
/// </summary>
public sealed class VocabularyMergerTests
{
    private static readonly PreparationOptions Options = new();

    private static VocabularyMember M(string name, string directory) => new(name, directory);

    [Theory]
    [InlineData("ParcelService", "parcel,service")]
    [InlineData("HTTPServerFactory", "http,server,factory")]
    [InlineData("parcel_tracking.v2Impl", "parcel,tracking,v,impl")]
    [InlineData("ShipmentRepositories", "shipment,repository")]
    [InlineData("Ubicación", "ubicacion")]
    [InlineData("tiposUbicaciones", "tipo,ubicacion")]
    [InlineData("razonesDeVersiones", "razon,de,version")]
    [InlineData("Status", "status")]
    [InlineData("", "")]
    public void Names_split_by_case_separators_digits_and_lose_their_plural(string text, string expected) =>
        Assert.Equal(expected, string.Join(',', NameTokenizer.Tokenize(text)));

    [Fact]
    public void Distinct_tokens_keep_the_order_of_first_appearance()
    {
        Assert.Equal(["parcel", "list", "action"], NameTokenizer.DistinctTokens("ParcelListAction", "parcel/Action"));
    }

    [Fact]
    public void The_index_drops_generic_unique_and_ubiquitous_terms()
    {
        var members = new List<VocabularyMember>();
        // «parcel» en 3 de 8, «ledger» en 2, «shared» en 5 de 8 (más de la mitad), «once» en uno solo; «impl» y «dao» son genéricos.
        for (var i = 0; i < 3; i++)
            members.Add(M($"Parcel{i}Dao", "app/shared"));
        for (var i = 0; i < 2; i++)
            members.Add(M($"Ledger{i}Impl", "app/shared"));
        members.Add(M("Once", "app/other"));
        members.Add(M("Plain", "app/other"));
        members.Add(M("Plain2", "app/other"));

        var index = new VocabularyIndex(members, Options);

        Assert.Equal(3, index.DocumentFrequency("parcel"));
        Assert.Equal(2, index.DocumentFrequency("ledger"));
        Assert.Equal(0, index.DocumentFrequency("shared"));
        Assert.Equal(0, index.DocumentFrequency("once"));
        Assert.Equal(0, index.DocumentFrequency("impl"));
        Assert.Equal(0, index.DocumentFrequency("dao"));
        Assert.Equal(["parcel"], index.TokensOf(0));
    }

    [Fact]
    public void A_term_carried_by_more_members_than_any_subsystem_can_hold_is_dropped()
    {
        var members = Enumerable.Range(0, 20).Select(i => M($"Parcel{i}", $"app/p{i % 4}")).Concat(Enumerable.Range(0, 20).Select(i => M($"Other{i}", "app/o"))).ToList();

        var unlimited = new VocabularyIndex(members, Options);
        var capped = new VocabularyIndex(members, Options, maxTermMembers: 10);

        Assert.Equal(20, unlimited.DocumentFrequency("parcel"));
        Assert.Equal(0, capped.DocumentFrequency("parcel"));
    }

    [Fact]
    public void Divergence_is_zero_in_one_folder_one_between_siblings_and_grows_with_distance()
    {
        var index = new VocabularyIndex(
        [
            M("ParcelA", "app/domain/ship"), M("ParcelB", "app/domain/ship"),
            M("ParcelC", "app/domain/ship"), M("ParcelD", "app/domain/track"),
            M("ParcelE", "app/web/actions"), M("ParcelF", "app/web/forms"),
            M("Zq0", "app/x"), M("Zq1", "app/x"), M("Zq2", "app/y"), M("Zq3", "app/y"), M("Zq4", "app/y"), M("Zq5", "app/y"),
            M("Zq6", "app/y"), M("Zq7", "app/y"), M("Zq8", "app/y"), M("Zq9", "app/y"), M("Zq10", "app/y"), M("Zq11", "app/y"),
            M("Zq12", "app/y"), M("Zq13", "app/y")
        ], Options);

        Assert.Equal(0, index.Divergence("parcel", [0, 1, 2]));
        Assert.Equal(1, index.Divergence("parcel", [0, 3]), 6);
        // app/domain/ship (2 niveles por debajo de «app») y app/web/forms (2): distancia 2 a su ancestro común «app».
        Assert.Equal(2, index.Divergence("parcel", [0, 5]), 6);
    }

    [Fact]
    public void A_capability_spread_over_two_distant_branches_is_merged_into_one_group()
    {
        // Una capacidad con clases en web/ y en domain/, y otras dos capacidades con sus carpetas. Ninguna carpeta se llama «parcel».
        var members = new List<VocabularyMember>();
        var parcelWeb = AddAll(members, [("ParcelListAction", "app/web/actions"), ("ParcelForm", "app/web/forms"), ("ParcelSearchAction", "app/web/actions")]);
        var parcelDomain = AddAll(members, [("ParcelService", "app/domain/shipping/transit"), ("ParcelPolicy", "app/domain/shipping/transit"), ("ParcelStore", "app/domain/shipping/transit")]);
        var ledger = AddAll(members, [("LedgerService", "app/domain/shipping/ledger"), ("LedgerEntry", "app/domain/shipping/ledger"), ("LedgerStore", "app/domain/shipping/ledger")]);
        var tenant = AddAll(members, [("TenantService", "app/domain/tenancy"), ("TenantRecord", "app/domain/tenancy"), ("TenantStore", "app/domain/tenancy")]);
        var index = new VocabularyIndex(members, Options);

        var groups = VocabularyMerger.Merge(index, [parcelWeb, parcelDomain, ledger, tenant], Options);

        Assert.Equal(groups[0], groups[1]);
        Assert.Equal([0, 0, 1, 2], groups);
    }

    [Fact]
    public void Two_capabilities_under_a_shared_parent_folder_are_not_merged_by_a_folder_term()
    {
        // «shipping» está en las carpetas de las dos, pero las separa un solo nivel (hermanas): estructura, no vocabulario.
        var members = new List<VocabularyMember>();
        var parcel = AddAll(members, [("ParcelService", "app/shipping/parcel"), ("ParcelPolicy", "app/shipping/parcel"), ("ParcelStore", "app/shipping/parcel")]);
        var ledger = AddAll(members, [("LedgerService", "app/shipping/ledger"), ("LedgerEntry", "app/shipping/ledger"), ("LedgerStore", "app/shipping/ledger")]);
        var filler = AddAll(members, [("PlainA", "app/other"), ("PlainB", "app/other"), ("PlainC", "app/other"), ("PlainD", "app/other")]);
        Neutral(members, 14);
        var index = new VocabularyIndex(members, Options);

        // «shipping» lo llevan los seis miembros (cobertura total en las dos) y aun así no las funde.
        Assert.Equal(6, index.DocumentFrequency("shipping"));
        Assert.Equal([0, 1, 2], VocabularyMerger.Merge(index, [parcel, ledger, filler], Options));
    }

    [Fact]
    public void A_term_that_does_not_reach_the_threshold_in_both_communities_does_not_merge()
    {
        var members = new List<VocabularyMember>();
        // Solo 2 de los 6 miembros de la primera comunidad llevan «parcel»: el 33 % no llega al 50 %.
        var mixed = AddAll(members, [("ParcelA", "app/web/a"), ("ParcelB", "app/web/a"), ("AlphaX", "app/web/a"), ("BetaX", "app/web/a"), ("GammaX", "app/web/a"), ("DeltaX", "app/web/a")]);
        var pure = AddAll(members, [("ParcelC", "app/domain/z/deep"), ("ParcelD", "app/domain/z/deep"), ("ParcelE", "app/domain/z/deep")]);
        Neutral(members, 12);
        var index = new VocabularyIndex(members, Options);

        Assert.Equal([0, 1], VocabularyMerger.Merge(index, [mixed, pure], Options));
        Assert.Equal([0, 0], VocabularyMerger.Merge(index, [mixed, pure], new PreparationOptions { VocabularyMergeThreshold = 0.3 }));
    }

    [Fact]
    public void A_merge_that_would_exceed_the_maximum_size_is_not_done()
    {
        var members = new List<VocabularyMember>();
        var one = AddAll(members, [("ParcelA", "app/web/a"), ("ParcelB", "app/web/a"), ("ParcelC", "app/web/a")]);
        var two = AddAll(members, [("ParcelD", "app/domain/z/deep"), ("ParcelE", "app/domain/z/deep"), ("ParcelF", "app/domain/z/deep")]);
        var other = AddAll(members, [("PlainA", "app/o"), ("PlainB", "app/o"), ("PlainC", "app/o"), ("PlainD", "app/o")]);
        Neutral(members, 10);
        var index = new VocabularyIndex(members, Options);

        Assert.Equal([0, 0, 1], VocabularyMerger.Merge(index, [one, two, other], Options));
        Assert.Equal([0, 1, 2], VocabularyMerger.Merge(index, [one, two, other], Options, maxMergedSize: 5));
    }

    [Fact]
    public void A_chain_of_communities_that_share_the_term_ends_in_one_group()
    {
        var members = new List<VocabularyMember>();
        var a = AddAll(members, [("ParcelA", "app/x/one"), ("ParcelB", "app/x/one"), ("ParcelC", "app/x/one")]);
        var b = AddAll(members, [("ParcelD", "app/y/two/deep"), ("ParcelE", "app/y/two/deep"), ("ParcelF", "app/y/two/deep")]);
        var c = AddAll(members, [("ParcelG", "app/z/three/deep"), ("ParcelH", "app/z/three/deep"), ("ParcelI", "app/z/three/deep")]);
        var other = AddAll(members, [("PlainA", "app/o"), ("PlainB", "app/o"), ("PlainC", "app/o"), ("PlainD", "app/o")]);
        Neutral(members, 20);
        var index = new VocabularyIndex(members, Options);

        var groups = VocabularyMerger.Merge(index, [a, b, c, other], Options);

        Assert.Equal([0, 0, 0, 1], groups);
        Assert.Equal(groups, VocabularyMerger.Merge(index, [a, b, c, other], Options));
    }

    /// <summary>Miembros sin ningún término útil (nombres de dos letras): hacen de repositorio grande sin añadir vocabulario.</summary>
    private static void Neutral(List<VocabularyMember> members, int count)
    {
        for (var i = 0; i < count; i++)
            members.Add(M($"Zq{i}", $"app/n{i % 5}"));
    }

    private static List<int> AddAll(List<VocabularyMember> members, IEnumerable<(string Name, string Directory)> items)
    {
        var indices = new List<int>();
        foreach (var (name, directory) in items)
        {
            indices.Add(members.Count);
            members.Add(M(name, directory));
        }
        return indices;
    }
}

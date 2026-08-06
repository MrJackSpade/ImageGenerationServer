using ImageGen.Domain.Repositories;

namespace ImageGen.Tests;

/// <summary>
/// The DB-backed workflow variants: duplicates of shipped configurations held per machine. Like the catalogue
/// overrides they run on whichever engine the suite is pointed at, so the same assertions cover SQL Server and SQLite.
/// </summary>
[Collection("db")]
public sealed class WorkflowVariantRepositoryTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private IWorkflowVariantRepository Repo => fixture.WorkflowVariants;

    [Fact]
    public async Task A_variant_round_trips()
    {
        const string machine = "var-roundtrip";
        await Repo.AddAsync(machine, new WorkflowVariant("anima-2", "anima", "Anima hi-res", """{"steps":40}"""), Ct);

        IReadOnlyList<WorkflowVariant> all = await Repo.VariantsAsync(machine, Ct);
        WorkflowVariant v = Assert.Single(all);
        Assert.Equal("anima-2", v.VariantId);
        Assert.Equal("anima", v.BaseConfigId);
        Assert.Equal("Anima hi-res", v.FriendlyName);
        Assert.Equal("""{"steps":40}""", v.ParamsJson);
    }

    /// <summary>A variant describes this machine's catalogue, so two machines sharing one database must not see each
    /// other's — the same isolation bindings and overrides have.</summary>
    [Fact]
    public async Task Variants_are_isolated_per_machine()
    {
        await Repo.AddAsync("box-a", new WorkflowVariant("cfg-2", "cfg", "A", "{}"), Ct);
        await Repo.AddAsync("box-b", new WorkflowVariant("cfg-3", "cfg", "B", "{}"), Ct);

        Assert.Equal("cfg-2", Assert.Single(await Repo.VariantsAsync("box-a", Ct)).VariantId);
        Assert.Equal("cfg-3", Assert.Single(await Repo.VariantsAsync("box-b", Ct)).VariantId);
    }

    [Fact]
    public async Task Deleting_a_variant_removes_it()
    {
        const string machine = "var-delete";
        await Repo.AddAsync(machine, new WorkflowVariant("cfg-2", "cfg", "V", "{}"), Ct);
        await Repo.DeleteAsync(machine, "cfg-2", Ct);

        Assert.Empty(await Repo.VariantsAsync(machine, Ct));
    }

    [Fact]
    public async Task Deleting_an_unknown_variant_is_a_no_op()
    {
        const string machine = "var-delete-missing";
        await Repo.AddAsync(machine, new WorkflowVariant("keep-2", "keep", "Keep", "{}"), Ct);
        await Repo.DeleteAsync(machine, "never-existed", Ct);

        Assert.Equal("keep-2", Assert.Single(await Repo.VariantsAsync(machine, Ct)).VariantId);
    }

    [Fact]
    public async Task An_unknown_machine_has_no_variants() =>
        Assert.Empty(await Repo.VariantsAsync("never-seen", Ct));
}

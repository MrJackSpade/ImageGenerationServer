using ImageGen.Comfy;

namespace ImageGen.Tests;

/// <summary>
/// Issue #163: each workflow configuration resolves to exactly ONE authoritative kind — the value the catalog API emits
/// and both the workflows-page badge and the edit-page tab routing read. The class knows only Generate/Edit and the
/// dedicated Inpaint/Outpaint; the catalog folds in the config's media, edit-group and effect-type to name the rest.
/// </summary>
public sealed class WorkflowKindResolutionTests
{
    private sealed class StubWorkflow(
        WorkflowKind kind, WorkflowMedia media = WorkflowMedia.Image, WorkflowMedia source = WorkflowMedia.Image) : IWorkflow
    {
        public string Name => "stub";
        public WorkflowKind Kind => kind;
        public WorkflowMedia Media => media;
        public WorkflowMedia SourceMedia => source;
        public bool PromptDirectsMotion => false;
        public IReadOnlyList<ParamSpec> Schema => [];
        public ComfyWorkflowGraph Build(IReadOnlyDictionary<string, object?> p, ResolvedRequirements req, WorkflowInputs inputs) =>
            throw new NotSupportedException("test stub does not build");
    }

    private static WorkflowConfiguration Cfg(string? editGroup = null, string? effectType = null) =>
        new() { Id = "c", WorkflowName = "w", EditGroup = editGroup, EffectType = effectType };

    [Fact]
    public void A_generate_class_always_resolves_to_generate()
    {
        // Even with an edit-group/effect on the config, a generate class stays generate — those only refine an editor.
        Assert.Equal(WorkflowKind.Generate,
            WorkflowCatalogService.ResolveKind(Cfg(editGroup: "Redraw", effectType: "Line art"), new StubWorkflow(WorkflowKind.Generate)));
    }

    [Theory]
    [InlineData(WorkflowKind.Inpaint)]
    [InlineData(WorkflowKind.Outpaint)]
    public void A_dedicated_class_kind_is_taken_verbatim(WorkflowKind classKind) =>
        Assert.Equal(classKind, WorkflowCatalogService.ResolveKind(Cfg(), new StubWorkflow(classKind)));

    [Fact]
    public void An_edit_class_with_a_video_source_resolves_to_videoedit() =>
        Assert.Equal(WorkflowKind.VideoEdit,
            WorkflowCatalogService.ResolveKind(Cfg(), new StubWorkflow(WorkflowKind.Edit, media: WorkflowMedia.Video, source: WorkflowMedia.Video)));

    [Fact]
    public void An_edit_class_that_outputs_video_from_a_still_resolves_to_animate() =>
        Assert.Equal(WorkflowKind.Animate,
            WorkflowCatalogService.ResolveKind(Cfg(), new StubWorkflow(WorkflowKind.Edit, media: WorkflowMedia.Video)));

    [Theory]
    [InlineData("Redraw", WorkflowKind.Redraw)]
    [InlineData("Upscale", WorkflowKind.Upscale)]
    public void An_edit_group_promotes_to_its_own_kind(string editGroup, WorkflowKind expected) =>
        Assert.Equal(expected, WorkflowCatalogService.ResolveKind(Cfg(editGroup: editGroup), new StubWorkflow(WorkflowKind.Edit)));

    [Fact]
    public void An_effect_type_resolves_to_effect() =>
        Assert.Equal(WorkflowKind.Effect,
            WorkflowCatalogService.ResolveKind(Cfg(effectType: "Line art"), new StubWorkflow(WorkflowKind.Edit)));

    [Fact]
    public void A_plain_edit_class_with_no_config_hints_resolves_to_edit() =>
        Assert.Equal(WorkflowKind.Edit, WorkflowCatalogService.ResolveKind(Cfg(), new StubWorkflow(WorkflowKind.Edit)));

    [Fact]
    public void A_video_source_wins_over_an_edit_group_or_effect()
    {
        // Precedence: the media split is decided before the edit-group/effect refinement.
        Assert.Equal(WorkflowKind.VideoEdit, WorkflowCatalogService.ResolveKind(
            Cfg(editGroup: "Redraw", effectType: "Line art"),
            new StubWorkflow(WorkflowKind.Edit, media: WorkflowMedia.Video, source: WorkflowMedia.Video)));
    }

    [Fact]
    public void A_reference_taking_workflow_gets_the_ref_tag_first()
    {
        // "Ref" is DERIVED from the reference capability, not authored — and it leads, so it reads first in the picker.
        string[]? tags = WorkflowCatalogService.ComposeBaseTags(
            new ModelCard { Tags = ["portrait"] }, takesReference: true, isInpaint: false);
        Assert.NotNull(tags);
        Assert.Equal(["Ref", "portrait"], tags);
    }

    [Fact]
    public void An_inpaint_workflow_gets_the_inpaint_tag()
    {
        string[]? tags = WorkflowCatalogService.ComposeBaseTags(
            new ModelCard(), takesReference: false, isInpaint: true);
        Assert.NotNull(tags);
        Assert.Equal(["Inpaint"], tags);
    }

    [Fact]
    public void A_non_reference_workflow_carries_only_its_authored_tags()
    {
        string[]? authored = WorkflowCatalogService.ComposeBaseTags(
            new ModelCard { Tags = ["portrait"] }, takesReference: false, isInpaint: false);
        Assert.NotNull(authored);
        Assert.Equal(["portrait"], authored);
        // Nothing authored and no capability → no base tags at all (null, like the other empty card fields).
        Assert.Null(WorkflowCatalogService.ComposeBaseTags(new ModelCard(), takesReference: false, isInpaint: false));
    }

    [Fact]
    public void A_card_that_already_spells_ref_is_not_duplicated()
    {
        string[]? tags = WorkflowCatalogService.ComposeBaseTags(
            new ModelCard { Tags = ["Ref"] }, takesReference: true, isInpaint: false);
        Assert.NotNull(tags);
        Assert.Equal(["Ref"], tags);
    }

    [Theory]
    [InlineData(WorkflowKind.Generate, "generate")]
    [InlineData(WorkflowKind.Edit, "edit")]
    [InlineData(WorkflowKind.Inpaint, "inpaint")]
    [InlineData(WorkflowKind.Outpaint, "outpaint")]
    [InlineData(WorkflowKind.Redraw, "redraw")]
    [InlineData(WorkflowKind.Upscale, "upscale")]
    [InlineData(WorkflowKind.Effect, "effect")]
    [InlineData(WorkflowKind.Animate, "animate")]
    [InlineData(WorkflowKind.VideoEdit, "videoedit")]
    public void Each_kind_maps_to_its_wire_token(WorkflowKind kind, string token) =>
        Assert.Equal(token, WorkflowCatalogService.KindToken(kind));
}

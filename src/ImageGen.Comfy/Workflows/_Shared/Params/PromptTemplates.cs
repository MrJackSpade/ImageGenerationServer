using ImageGen.Application.Rendering;
using NetJinja;
using NetJinja.Runtime;

namespace ImageGen.Comfy;

/// <summary>The workflow-level positive-prompt template. It is a normal configuration parameter, so a workflow can
/// ship one exposed, hidden-but-revealable, or locked just like every sampling control. Rendering happens in the
/// client after its existing prompt rules and immediately before the finalized text enters the workflow graph.</summary>
internal static class PromptTemplates
{
    public static readonly ParamSpec Schema = new()
    {
        Key = WorkflowParamKeys.PromptTemplate,
        Type = ParamType.Multiline,
        Label = "Prompt template",
        Help = "Jinja template for the final positive prompt. Use {{ prompt }} for plain text or {{ prompt | tojson }} inside JSON.",
    };

    /// <summary>Render a Jinja template with the submitted prompt as <c>prompt</c>. Missing/blank means identity, so
    /// configurations that do not declare a template, or explicitly override one with blank, use the regular prompt.</summary>
    public static string Render(string? source, string prompt, string workflowName)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return prompt;
        }

        try
        {
            JinjaEnvironment environment = Jinja.CreateEnvironment();
            environment.StrictUndefined = true;
            Template template = environment.FromString(source);
            return template.Render(new Dictionary<string, object?> { [PromptTemplateText.PromptVariable] = prompt });
        }
        // NetJinja exposes several syntax/render exception types. Keep that implementation detail behind the render
        // boundary and give every caller the same workflow-specific validation error.
        catch (Exception ex)
        {
            throw new RenderValidationException($"Workflow '{workflowName}' has an invalid prompt template: {ex.Message}", ex);
        }
    }

    /// <summary>Parse and render once on the settings write path, so a typo is refused where it is entered instead of
    /// being stored and discovered after a render is queued.</summary>
    public static void Validate(string source, string workflowName) => _ = Render(source, PromptTemplateText.ValidationPrompt, workflowName);
}

/// <summary>Non-user text tokens used by prompt-template rendering and validation.</summary>
file static class PromptTemplateText
{
    public const string PromptVariable = "prompt";
    public const string ValidationPrompt = "validation prompt";
}

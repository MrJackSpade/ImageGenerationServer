using ImageGen.Application.Services;
using ImageGen.Application.Tags;
using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;

namespace ImageGen.Api.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this RouteGroupBuilder api)
    {
        // This user's account-level preferences, as one read for the whole app. Per user, so they follow the user
        // across devices. There is no PUT counterpart: every writable preference below owns its own route and its
        // own column, so one autosave can never clobber another's.
        api.MapGet("/settings", async (HttpContext context, UserService users) =>
        {
            var userId = context.User.GetUserId()!.Value;
            var user = await users.GetByIdAsync(userId, context.RequestAborted);
            // The workflow relations are read separately — they are rows, not columns on the user, and are wanted
            // here and nowhere else. They go out as real arrays/maps; the client no longer parses a string.
            var workflows = await users.GetWorkflowPrefsAsync(userId, context.RequestAborted);
            return Results.Ok(new { composerPrefs = user?.ComposerPrefs, editPrefs = user?.EditPrefs, bookmarkPrefs = user?.BookmarkPrefs,
                favoriteWorkflowIds = workflows.Favorites, customWorkflowTags = workflows.Tags,
                hiddenWorkflowIds = workflows.Hidden, hiddenApiWorkflowIds = workflows.HiddenApi,
                // The generation mask, RESOLVED (an unset column reads as the default) plus the switchable types, so the
                // settings page renders the real choices from the model's own list instead of hardcoding a copy of it.
                generationTagTypes = GenerationTagTypes.Resolve(user?.GenerationTagTypes),
                generationTagTypeOptions = GenerationTagTypes.Selectable });
        });

        // Composer state (one PUT, one column).
        api.MapPut("/settings/composer", async (HttpContext context, UserService users) =>
        {
            var request = await Json.ReadAsync<ComposerPrefsRequest>(context);
            if (request is null) return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            await users.SetComposerPrefsAsync(userId, request.ComposerPrefs, context.RequestAborted);
            return Results.NoContent();
        });

        // The editor's state blob (mode/workflows/params/brush), likewise on its own route (one PUT, one column),
        // so the editor autosave can't clobber the composer's and vice versa.
        api.MapPut("/settings/edit-prefs", async (HttpContext context, UserService users) =>
        {
            var request = await Json.ReadAsync<EditPrefsRequest>(context);
            if (request is null) return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            await users.SetEditPrefsAsync(userId, request.EditPrefs, context.RequestAborted);
            return Results.NoContent();
        });

        // The bookmarks page's folded sections, on its own route and column like the two above.
        api.MapPut("/settings/bookmarks", async (HttpContext context, UserService users) =>
        {
            var request = await Json.ReadAsync<BookmarkPrefsRequest>(context);
            if (request is null) return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            await users.SetBookmarkPrefsAsync(userId, request.BookmarkPrefs, context.RequestAborted);
            return Results.NoContent();
        });

        // Favorited workflow ids (opaque JSON array) — one PUT, one column.
        api.MapPut("/settings/favorites", async (HttpContext context, UserService users) =>
        {
            var request = await Json.ReadAsync<FavoriteWorkflowsRequest>(context);
            if (request is null) return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            await users.SetFavoriteWorkflowsAsync(userId, request.FavoriteWorkflowIds, context.RequestAborted);
            return Results.NoContent();
        });

        // Custom per-workflow tags (opaque JSON map, encrypted at rest) — one PUT, one column.
        api.MapPut("/settings/workflow-tags", async (HttpContext context, UserService users) =>
        {
            var request = await Json.ReadAsync<WorkflowTagsRequest>(context);
            if (request is null) return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            await users.SetWorkflowTagsAsync(userId, request.CustomWorkflowTags, context.RequestAborted);
            return Results.NoContent();
        });

        // Workflows hidden from the UI picker (opaque JSON array) — one PUT, one column.
        api.MapPut("/settings/hidden", async (HttpContext context, UserService users) =>
        {
            var request = await Json.ReadAsync<HiddenWorkflowsRequest>(context);
            if (request is null) return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            await users.SetHiddenWorkflowsAsync(userId, request.HiddenWorkflowIds, context.RequestAborted);
            return Results.NoContent();
        });

        // Workflows hidden from the API workflow list (opaque JSON array) — separate from the UI-picker set above.
        api.MapPut("/settings/hidden-api", async (HttpContext context, UserService users) =>
        {
            var request = await Json.ReadAsync<HiddenApiWorkflowsRequest>(context);
            if (request is null) return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            await users.SetHiddenApiWorkflowsAsync(userId, request.HiddenApiWorkflowIds, context.RequestAborted);
            return Results.NoContent();
        });

        // The generation mask — which tag types the random-prompt model may emit — one PUT, one column. Bounds ONLY
        // random-prompt generation; tag autocomplete keeps ranking every type regardless of what is set here.
        api.MapPut("/settings/generation-tag-types", async (HttpContext context, UserService users) =>
        {
            var request = await Json.ReadAsync<GenerationTagTypesRequest>(context);
            if (request is null) return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            // An unknown type name is rejected, not dropped: a dropped name would read as "switched off" and quietly
            // change what the model generates.
            var error = await users.SetGenerationTagTypesAsync(userId, request.GenerationTagTypes, context.RequestAborted);
            return error is null ? Results.NoContent() : Results.BadRequest(new { error });
        });
    }
}

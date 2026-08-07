using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;
using ImageGen.Application.Services;
using ImageGen.Application.Tags;
using ImageGen.Domain.Entities;

namespace ImageGen.Api.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this RouteGroupBuilder api)
    {
        // This user's account-level preferences, as one read for the whole app. Per user, so they follow the user
        // across devices. There is no PUT counterpart: every writable preference below owns its own route and its
        // own column, so one autosave can never clobber another's.
        _ = api.MapGet(Routes.Settings, async (HttpContext context, UserService users) =>
        {
            long userId = context.User.GetRequiredUserId();
            User? user = await users.GetByIdAsync(userId, context.RequestAborted);
            // An authenticated request whose user row is gone is a stale session, not an empty account: 401 sends the
            // caller to re-authenticate, where returning blank settings would instead read as a real (empty) account.
            if (user is null)
            {
                return Results.Unauthorized();
            }
            // The workflow relations are read separately — they are rows, not columns on the user, and are wanted
            // here and nowhere else. They go out as real arrays/maps; the client no longer parses a string.
            UserWorkflowPrefs workflows = await users.GetWorkflowPrefsAsync(userId, context.RequestAborted);
            return Results.Ok(new
            {
                composerPrefs = user.ComposerPrefs,
                editPrefs = user.EditPrefs,
                bookmarkPrefs = user.BookmarkPrefs,
                paramVisibilityPrefs = user.ParamVisibilityPrefs,
                pinBookmarks = user.PinBookmarkSuggestions,
                favoriteWorkflowIds = workflows.Favorites,
                customWorkflowTags = workflows.Tags,
                hiddenWorkflowIds = workflows.Hidden,
                hiddenApiWorkflowIds = workflows.HiddenApi,
                // The generation mask, RESOLVED (an unset column reads as the default) plus the switchable types, so the
                // settings page renders the real choices from the model's own list instead of hardcoding a copy of it.
                generationTagTypes = GenerationTagTypes.Resolve(user.GenerationTagTypes),
                generationTagTypeOptions = GenerationTagTypes.Selectable
            });
        });

        // Composer state (one PUT, one column).
        _ = api.MapPut(Routes.Composer, async (HttpContext context, UserService users) =>
        {
            ComposerPrefsRequest? request = await Json.ReadAsync<ComposerPrefsRequest>(context);
            if (request is null)
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await users.SetComposerPrefsAsync(userId, request.ComposerPrefs, context.RequestAborted);
            return Results.NoContent();
        });

        // The editor's state blob (mode/workflows/params/brush), likewise on its own route (one PUT, one column),
        // so the editor autosave can't clobber the composer's and vice versa.
        _ = api.MapPut(Routes.EditPrefs, async (HttpContext context, UserService users) =>
        {
            EditPrefsRequest? request = await Json.ReadAsync<EditPrefsRequest>(context);
            if (request is null)
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await users.SetEditPrefsAsync(userId, request.EditPrefs, context.RequestAborted);
            return Results.NoContent();
        });

        // The bookmarks page's folded sections, on its own route and column like the two above.
        _ = api.MapPut(Routes.Bookmarks, async (HttpContext context, UserService users) =>
        {
            BookmarkPrefsRequest? request = await Json.ReadAsync<BookmarkPrefsRequest>(context);
            if (request is null)
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await users.SetBookmarkPrefsAsync(userId, request.BookmarkPrefs, context.RequestAborted);
            return Results.NoContent();
        });

        // Favorited workflow ids (opaque JSON array) — one PUT, one column.
        _ = api.MapPut(Routes.Favorites, async (HttpContext context, UserService users) =>
        {
            FavoriteWorkflowsRequest? request = await Json.ReadAsync<FavoriteWorkflowsRequest>(context);
            if (request is null)
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await users.SetFavoriteWorkflowsAsync(userId, request.FavoriteWorkflowIds, context.RequestAborted);
            return Results.NoContent();
        });

        // Custom per-workflow tags (opaque JSON map, encrypted at rest) — one PUT, one column.
        _ = api.MapPut(Routes.WorkflowTags, async (HttpContext context, UserService users) =>
        {
            WorkflowTagsRequest? request = await Json.ReadAsync<WorkflowTagsRequest>(context);
            if (request is null)
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await users.SetWorkflowTagsAsync(userId, request.CustomWorkflowTags, context.RequestAborted);
            return Results.NoContent();
        });

        // Workflows hidden from the UI picker (opaque JSON array) — one PUT, one column.
        _ = api.MapPut(Routes.Hidden, async (HttpContext context, UserService users) =>
        {
            HiddenWorkflowsRequest? request = await Json.ReadAsync<HiddenWorkflowsRequest>(context);
            if (request is null)
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await users.SetHiddenWorkflowsAsync(userId, request.HiddenWorkflowIds, context.RequestAborted);
            return Results.NoContent();
        });

        // Workflows hidden from the API workflow list (opaque JSON array) — separate from the UI-picker set above.
        _ = api.MapPut(Routes.HiddenApi, async (HttpContext context, UserService users) =>
        {
            HiddenApiWorkflowsRequest? request = await Json.ReadAsync<HiddenApiWorkflowsRequest>(context);
            if (request is null)
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await users.SetHiddenApiWorkflowsAsync(userId, request.HiddenApiWorkflowIds, context.RequestAborted);
            return Results.NoContent();
        });

        // The user's per-workflow parameter-visibility overrides (issue #191) — one PUT, one column. An opaque blob
        // the client merges over each workflow's shipped exposed/hidden params; the server never reads it (the
        // submit path is gated by the catalog's locked state, not by visibility).
        _ = api.MapPut(Routes.ParamVisibility, async (HttpContext context, UserService users) =>
        {
            ParamVisibilityPrefsRequest? request = await Json.ReadAsync<ParamVisibilityPrefsRequest>(context);
            if (request is null)
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await users.SetParamVisibilityPrefsAsync(userId, request.ParamVisibilityPrefs, context.RequestAborted);
            return Results.NoContent();
        });

        // The generation mask — which tag types the random-prompt model may emit — one PUT, one column. Bounds ONLY
        // random-prompt generation; tag autocomplete keeps ranking every type regardless of what is set here.
        _ = api.MapPut(Routes.GenerationTagTypes, async (HttpContext context, UserService users) =>
        {
            GenerationTagTypesRequest? request = await Json.ReadAsync<GenerationTagTypesRequest>(context);
            if (request is null)
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            // An unknown type name is rejected, not dropped: a dropped name would read as "switched off" and quietly
            // change what the model generates.
            string? error = await users.SetGenerationTagTypesAsync(userId, request.GenerationTagTypes, context.RequestAborted);
            return error is null ? Results.NoContent() : Results.BadRequest(new { error });
        });

        // Whether autocomplete pins the user's matching bookmarks to the top — one PUT, one column, like the rest.
        _ = api.MapPut(Routes.PinBookmarks, async (HttpContext context, UserService users) =>
        {
            PinBookmarksRequest? request = await Json.ReadAsync<PinBookmarksRequest>(context);
            if (request is null)
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await users.SetPinBookmarkSuggestionsAsync(userId, request.PinBookmarks, context.RequestAborted);
            return Results.NoContent();
        });
    }

    /// <summary>Route templates for the settings endpoints.</summary>
    private static class Routes
    {
        /// <summary>The whole account's preferences, read in one GET.</summary>
        public const string Settings = "/settings";

        /// <summary>The composer state blob.</summary>
        public const string Composer = "/settings/composer";

        /// <summary>The editor state blob.</summary>
        public const string EditPrefs = "/settings/edit-prefs";

        /// <summary>The bookmarks page's folded sections.</summary>
        public const string Bookmarks = "/settings/bookmarks";

        /// <summary>Favorited workflow ids.</summary>
        public const string Favorites = "/settings/favorites";

        /// <summary>Custom per-workflow tags.</summary>
        public const string WorkflowTags = "/settings/workflow-tags";

        /// <summary>Workflows hidden from the UI picker.</summary>
        public const string Hidden = "/settings/hidden";

        /// <summary>Workflows hidden from the API workflow list.</summary>
        public const string HiddenApi = "/settings/hidden-api";

        /// <summary>The user's per-workflow parameter-visibility overrides.</summary>
        public const string ParamVisibility = "/settings/param-visibility";

        /// <summary>The generation mask (which tag types the random-prompt model may emit).</summary>
        public const string GenerationTagTypes = "/settings/generation-tag-types";

        /// <summary>Whether autocomplete pins the user's matching bookmarks to the top.</summary>
        public const string PinBookmarks = "/settings/pin-bookmarks";
    }
}

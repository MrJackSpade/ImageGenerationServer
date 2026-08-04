using ImageGen.Application.Security;
using ImageGen.Application.Services;

namespace ImageGen.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_then_verify_roundtrips()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");
        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
        Assert.False(PasswordHasher.Verify("wrong password", hash));
    }

    [Fact]
    public void Each_hash_is_salted_uniquely()
    {
        Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));
    }

    /// <summary>A damaged stored hash is a corrupt record, not a failed login. Answering false for it would tell the
    /// account holder "wrong password" about a row they could never authenticate against no matter what they typed, and
    /// give the operator nothing to find.</summary>
    [Theory]
    [InlineData("")]                                    // nothing stored
    [InlineData("not-a-valid-hash")]                    // not the $-separated shape at all
    [InlineData("SCRYPT$200000$c2FsdA==$aGFzaA==")]     // shape is right, algorithm is not ours
    [InlineData("PBKDF2$notanumber$c2FsdA==$aGFzaA==")] // unreadable iteration count
    [InlineData("PBKDF2$200000$not!base64$aGFzaA==")]   // corrupt salt
    [InlineData("PBKDF2$200000$c2FsdA==$not!base64")]   // corrupt digest
    public void Malformed_stored_hash_throws(string stored)
    {
        Assert.Throws<InvalidOperationException>(() => PasswordHasher.Verify("x", stored));
    }

    /// <summary>The one meaning left for false: the record is fine and the password simply does not match.</summary>
    [Fact]
    public void A_real_hash_still_answers_false_for_the_wrong_password()
    {
        Assert.False(PasswordHasher.Verify("wrong", PasswordHasher.Hash("right")));
    }
}

[Collection("db")]
public sealed class UserServiceTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private UserService Service() => new(fixture.Users, TimeProvider.System);

    [Fact]
    public async Task Register_creates_user_and_login_succeeds()
    {
        var svc = Service();
        var created = await svc.RegisterAsync("alice_auth", "s3cretpw!", "Alice", Ct);
        Assert.NotNull(created);
        Assert.Equal("alice_auth", created.Username);
        Assert.Equal("Alice", created.DisplayName);

        var ok = await svc.AuthenticateAsync("alice_auth", "s3cretpw!", Ct);
        Assert.NotNull(ok);
        Assert.Equal(created.Id, ok.Id);
    }

    /// <summary>
    /// Usernames collide regardless of case, and login finds the account however it is typed.
    ///
    /// <para>This is free on SQL Server, whose default collation is case-insensitive, and it is the behaviour the
    /// schema comment has always relied on. SQLite compares case-SENSITIVELY unless a column says
    /// <c>COLLATE NOCASE</c>, so without that one word 'Bob' and 'bob' would BOTH register — and which of them a
    /// login resolved to would depend on row order. Nothing else in the suite would have noticed. Runs against both
    /// engines, which is the only thing that makes it proof rather than an assertion about one of them.</para>
    /// </summary>
    [Fact]
    public async Task Username_uniqueness_and_login_ignore_case()
    {
        var svc = Service();
        Assert.NotNull(await svc.RegisterAsync("CaseUser", "password1", "Case", Ct));

        Assert.Null(await svc.RegisterAsync("caseuser", "password2", "Impostor", Ct));
        Assert.Null(await svc.RegisterAsync("CASEUSER", "password3", "Impostor", Ct));

        var ok = await svc.AuthenticateAsync("cAsEuSeR", "password1", Ct);
        Assert.NotNull(ok);
        Assert.Equal("CaseUser", ok.Username);   // the row that was actually stored, not the spelling typed
    }

    [Fact]
    public async Task Duplicate_username_is_rejected()
    {
        var svc = Service();
        Assert.NotNull(await svc.RegisterAsync("dup_auth", "password1", "", Ct));
        Assert.Null(await svc.RegisterAsync("dup_auth", "password2", "", Ct));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_fail()
    {
        var svc = Service();
        await svc.RegisterAsync("bob_auth", "rightpass1", "", Ct);
        Assert.Null(await svc.AuthenticateAsync("bob_auth", "wrongpass", Ct));
        Assert.Null(await svc.AuthenticateAsync("nobody_auth", "whatever1", Ct));
    }

    [Fact]
    public async Task Display_name_defaults_to_username()
    {
        var created = await Service().RegisterAsync("nodisplay_auth", "password1", "", Ct);
        Assert.NotNull(created);
        Assert.Equal("nodisplay_auth", created.DisplayName);
    }

    /// <summary>
    /// Favourites, hidden workflows and per-workflow labels are RELATIONS (user × workflow, user × workflow × tag)
    /// and are stored as rows, not as three JSON blobs on the user row. Each set is REPLACED wholesale, so unstarring
    /// actually removes the row rather than leaving a stale one behind — the thing a blob could never be asked about.
    /// </summary>
    [Fact]
    public async Task Workflow_relations_round_trip_and_replace_wholesale()
    {
        var svc = Service();
        var user = await svc.RegisterAsync("wfrel_auth", "password1", "", Ct);
        Assert.NotNull(user);

        await svc.SetFavoriteWorkflowsAsync(user.Id, ["anima", "flux2"], Ct);
        await svc.SetHiddenWorkflowsAsync(user.Id, ["slow-one"], Ct);
        await svc.SetWorkflowTagsAsync(user.Id, new Dictionary<string, IReadOnlyList<string>>
        {
            ["anima"] = ["favourite", "fast"],
        }, Ct);

        var prefs = await svc.GetWorkflowPrefsAsync(user.Id, Ct);
        Assert.Equal(["anima", "flux2"], prefs.Favorites.Order());
        Assert.Equal(["slow-one"], prefs.Hidden);
        Assert.Equal(["fast", "favourite"], prefs.Tags["anima"].Order());

        // A replace, not a merge: what is sent IS the set now.
        await svc.SetFavoriteWorkflowsAsync(user.Id, ["flux2"], Ct);
        await svc.SetWorkflowTagsAsync(user.Id, new Dictionary<string, IReadOnlyList<string>>(), Ct);

        var after = await svc.GetWorkflowPrefsAsync(user.Id, Ct);
        Assert.Equal(["flux2"], after.Favorites);
        Assert.Empty(after.Tags);
        Assert.Equal(["slow-one"], after.Hidden);   // untouched: each set has its own write
    }

    /// <summary>A workflow id is not sensitive and is stored PLAIN so it can be joined and counted; the user's own
    /// LABEL for it is their words, and is not.</summary>
    [Fact]
    public async Task A_workflow_id_is_queryable_and_its_label_is_encrypted()
    {
        var svc = Service();
        var user = await svc.RegisterAsync("wfcrypto_auth", "password1", "", Ct);
        Assert.NotNull(user);
        await svc.SetFavoriteWorkflowsAsync(user.Id, ["anima"], Ct);
        await svc.SetWorkflowTagsAsync(user.Id, new Dictionary<string, IReadOnlyList<string>>
        {
            ["anima"] = ["my private label"],
        }, Ct);

        await using var conn = await fixture.ConnectionFactory.OpenAsync(Ct);

        // The relation can be asked the question a blob could not: who favourited this workflow?
        await using (var cmd = conn.Command(
            "SELECT COUNT(*) FROM dbo.UserFavoriteWorkflow WHERE WorkflowId = 'anima' AND UserId = @id;"))
        {
            cmd.AddParam("@id", user.Id);
            Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct)));
        }

        await using (var cmd = conn.Command(
            "SELECT Tag FROM dbo.UserWorkflowTag WHERE UserId = @id;"))
        {
            cmd.AddParam("@id", user.Id);
            var scalar = await cmd.ExecuteScalarAsync(Ct);
            Assert.NotNull(scalar);
            Assert.NotEqual("my private label", (string)scalar);
        }
    }

    /// <summary>
    /// The bookmarks page's folded sections live on the ACCOUNT so they follow the user across devices — the reason
    /// this state is not in localStorage. It is an opaque blob to the server: what goes in comes back verbatim, and
    /// blank clears it back to nothing folded.
    /// </summary>
    [Fact]
    public async Task Bookmark_prefs_round_trip_on_the_account()
    {
        var svc = Service();
        var user = await svc.RegisterAsync("bmprefs_auth", "password1", "", Ct);
        Assert.NotNull(user);
        const string blob = """{"collapsed":["__global__/images","Landscapes","Landscapes/tags"]}""";

        await svc.SetBookmarkPrefsAsync(user.Id, blob, Ct);
        var stored = await svc.GetByIdAsync(user.Id, Ct);
        Assert.NotNull(stored);
        Assert.Equal(blob, stored.BookmarkPrefs);

        await svc.SetBookmarkPrefsAsync(user.Id, "  ", Ct);
        var cleared = await svc.GetByIdAsync(user.Id, Ct);
        Assert.NotNull(cleared);
        Assert.Null(cleared.BookmarkPrefs);
    }
}

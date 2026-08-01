namespace ImageGen.Tests;

/// <summary>
/// "Are there any accounts" — the question the sign-in page asks before showing itself. On a fresh install a
/// sign-in form is a dead end: there is nothing to sign in to, and the only answer it can give is "wrong username
/// or password", which is a lie about what is wrong.
/// </summary>
[Collection("db")]
public sealed class FirstAccountTests(TestDatabaseFixture db)
{
    private readonly TestDatabaseFixture _db = db;

    [Fact]
    public async Task An_account_makes_it_true_and_it_stays_true()
    {
        // The fixture is shared, so this cannot assert the empty case — every other test in the collection
        // creates users. That the answer is true once one exists is the half that is assertable here, and it is
        // the half the redirect depends on: a box with accounts must NOT bounce people to registration.
        await _db.NewUserAsync("first-account");

        Assert.True(await _db.Users.AnyExistAsync(CancellationToken.None));
        Assert.True(await _db.Users.AnyExistAsync(CancellationToken.None));
    }
}

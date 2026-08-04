using ImageGen.Application.Images;
using ImageGen.Application.Platform;

namespace ImageGen.Tests;

/// <summary>
/// The retention contract for render inputs. An upload is an input to work the API has ACCEPTED, and the queue it
/// waits in is durable and unbounded — so the store may never decide on its own to drop an id it has handed out.
/// <para>
/// Evicting the least-recently-used past a byte budget would let a bulk submission destroy its own earlier sources
/// long before the worker reached them, so every one of those accepted jobs would fail with "source image not
/// found". Pressure is answered at the DOOR (<see cref="SubmissionMemoryGate"/>), never by discarding work already
/// taken on.
/// </para>
/// </summary>
public sealed class UploadRetentionTests
{
    private static UploadedImage Image(int bytes) => new(new byte[bytes], "image/png", 8, 8);

    [Fact]
    public void Every_upload_resolves_however_many_follow_it()
    {
        var store = new InMemoryUploadStore();
        // Far past any byte budget an evicting store would keep: 64 x 1MB. The first id must answer exactly like the last.
        var ids = Enumerable.Range(0, 64).Select(_ => store.Add(Image(1024 * 1024))).ToList();

        Assert.Equal(64, store.Count);
        Assert.Equal(64L * 1024 * 1024, store.Bytes);
        foreach (var id in ids)
            Assert.NotNull(store.Get(id));
    }

    [Fact]
    public void A_cold_upload_survives_a_long_run_of_newer_ones()
    {
        var store = new InMemoryUploadStore();
        var first = store.Add(Image(4 * 1024 * 1024));
        // Never touched again — under an LRU this is precisely the entry that would be dropped.
        for (var i = 0; i < 200; i++) store.Add(Image(1024 * 1024));

        Assert.NotNull(store.Get(first));
    }

    [Fact]
    public void An_id_the_store_never_issued_is_null_not_an_error()
    {
        var store = new InMemoryUploadStore();
        Assert.Null(store.Get(Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void The_gate_admits_work_while_the_box_has_room()
    {
        var gate = new SubmissionMemoryGate(new FakeMemory(600L * 1024 * 1024), () => 500L * 1024 * 1024);
        Assert.Null(gate.Refusal());
    }

    [Fact]
    public void The_gate_refuses_below_the_floor_and_says_why()
    {
        var gate = new SubmissionMemoryGate(new FakeMemory(120L * 1024 * 1024), () => 500L * 1024 * 1024);

        var refusal = gate.Refusal();
        Assert.NotNull(refusal);
        // The caller is told both figures: a bare "unavailable" gives them nothing to act on.
        Assert.Contains("120 MB free", refusal);
        Assert.Contains("500 MB required", refusal);
    }

    [Fact]
    public void Exactly_at_the_floor_is_admitted()
    {
        var gate = new SubmissionMemoryGate(new FakeMemory(500L * 1024 * 1024), () => 500L * 1024 * 1024);
        Assert.Null(gate.Refusal());
    }

    [Fact]
    public void A_probe_that_cannot_read_the_box_fails_the_submission_rather_than_guessing()
    {
        var gate = new SubmissionMemoryGate(new BrokenMemory(), () => 500L * 1024 * 1024);
        // "Unknown" must never resolve to "plenty of room" — that is how an unrenderable job gets accepted.
        Assert.Throws<InvalidOperationException>(() => gate.Refusal());
    }

    private sealed class FakeMemory(long available) : ISystemMemory
    {
        public long AvailableBytes() => available;
    }

    private sealed class BrokenMemory : ISystemMemory
    {
        public long AvailableBytes() => throw new InvalidOperationException("cannot read available memory");
    }
}

namespace ImageGen.Application.Platform;

/// <summary>
/// Tells "the database is out of reach" apart from "this operation was wrong".
/// <para>The distinction is the difference between waiting and losing work. A constraint violation or a bad command
/// is a fault in the caller and must fail fast, loudly, where it happened. A database that cannot be reached is not
/// a property of the work at all — every job is equally affected, nothing the app could do instead is better, and
/// the condition resolves itself. Accepted work waits for it; it is never thrown away for it.</para>
/// <para>Lives here rather than being a <c>SqlException</c> check at each call site so the render path can ask the
/// question without knowing which database it is talking to.</para>
/// </summary>
public interface IDatabaseAvailability
{
    /// <summary>True when this exception means the database could not be reached, rather than that the operation
    /// itself was rejected. False for anything it cannot positively identify: guessing "unreachable" would turn a
    /// real bug into a silent, permanent wait.</summary>
    bool IsUnavailable(Exception ex);
}

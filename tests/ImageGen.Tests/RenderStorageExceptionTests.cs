using ImageGen.Application.Rendering;

namespace ImageGen.Tests;

public sealed class RenderStorageExceptionTests
{
    [Fact]
    public void Submission_failure_surfaces_the_complete_exception_message_chain()
    {
        InvalidOperationException provider = new("Invalid column name 'ModelPrompt'.");
        Exception write = new("JobSlot upsert failed.", provider);

        RenderStorageException error = RenderStorageException.Submission(write);

        Assert.Contains(typeof(Exception).Name, error.Message);
        Assert.Contains("JobSlot upsert failed.", error.Message);
        Assert.Contains(typeof(InvalidOperationException).Name, error.Message);
        Assert.Contains("Invalid column name 'ModelPrompt'.", error.Message);
        Assert.Same(write, error.InnerException);
    }
}

namespace ImageGen.Application.Civitai;

/// <summary>
/// Fills the machine-level CivitAI cache for LoRA files in the background. A surface that lists LoRAs (the picker, the
/// manager, the composer) calls <see cref="Request"/> for the files it's showing and returns immediately — nothing
/// blocks on hashing or a network round-trip. The populator hashes each not-yet-cached file, looks it up on CivitAI,
/// downloads its preview, and saves the result; the client polls until each file's row exists.
/// <para>Requests are COALESCED: asking for a file already queued or in flight does not start a second job, so
/// reloading the page (or several surfaces showing the same file) spawns no duplicate work.</para>
/// </summary>
public interface ILoraMetaPopulator
{
    /// <summary>Queue background population for any of these LoRA files not already cached or in flight. Returns at
    /// once; a no-op when CivitAI lookups are turned off.</summary>
    void Request(IReadOnlyCollection<string> loraNames);
}

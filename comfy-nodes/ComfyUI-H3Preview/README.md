# MiniMax H3 animated preview

`H3AnimatedPreview` wraps an H3 model's sampler without changing its denoising trajectory. At each configured
completed sampler step it extracts the video stream from H3's packed video/audio latent, uses the model's cheap
latent-to-RGB projection, thins the complete shot to at most 24 frames, and sends a 384px animated PNG through
ComfyUI's existing preview-image WebSocket event.

The sampler does not perform PIL resize or APNG compression. It queues a non-blocking GPU-to-pinned-CPU snapshot;
a bounded background worker encodes the latest pending preview and drops excess preview work if it falls behind.
Finishing the render cancels unfinished preview work rather than waiting for it.

No ComfyUI frontend or core patch is involved: browsers render APNG in the same `<img>` used for ordinary PNG
previews. `preview_steps` is a list of exact completed sampler steps; an empty list is a strict no-op. Preview
failures are logged and never fail the render.

# MiniMax H3 animated preview

`H3AnimatedPreview` wraps an H3 model's sampler without changing its denoising trajectory. At the configured
sampler-step interval it extracts the video stream from H3's packed video/audio latent, uses the model's cheap
latent-to-RGB projection, thins the complete shot to at most 24 frames, and sends a 384px animated PNG through
ComfyUI's existing preview-image WebSocket event.

No ComfyUI frontend or core patch is involved: browsers render APNG in the same `<img>` used for ordinary PNG
previews. `preview_every = 0` is a strict no-op. Preview failures are logged and never fail the render.

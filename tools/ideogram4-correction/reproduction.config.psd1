@{
    # ========================================================================
    # EDIT THIS FILE, NOT THE MAIN SCRIPT
    # ========================================================================

    # Custom: run every prompt below with every seed below.
    # Smoke: run one frozen known case.
    # FullValidation: run the frozen 16-case held-out panel.
    Mode = 'Custom'

    # Put each descriptive prompt in its own quoted string on a separate line.
    Prompts = @(
        'a black bear crossing a rocky river below a pine forest'
        # 'a ceramic teapot beside a folded linen napkin'
        # 'a stone footbridge over a shallow woodland creek in autumn'
    )

    # Every prompt is generated with every seed. Fixed seeds make baseline and
    # corrected outputs directly comparable. Put additional integers on new lines.
    Seeds = @(
        587896340
        # 12345
        # 67890
    )

    # Alternative to Prompts: set PromptFile to a text filename and make Prompts
    # an empty array. The path is resolved relative to this config file.
    PromptFile = ''

    # Leave Root empty to create "ideogram4-correction-reproduction" under the
    # current PowerShell directory. A relative path is resolved beside this file.
    Root = ''

    # Select the NVIDIA GPU and the localhost port used by the isolated server.
    GpuIndex = 0
    Port = 8194

    # Optional: point at an existing ComfyUI "models" directory. Files are
    # verified by hash and referenced read-only. Empty means download all models
    # into the isolated reproduction directory.
    ExistingModelRoot = ''
}

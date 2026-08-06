namespace ImageGen.Comfy;

/// <summary>
/// 24GB-tier VIDEO models the catalog lacked: the Wan 2.2 A14B MoE (two-expert high+low noise) for image→video and
/// text→video, and native text→video for HunyuanVideo 1.5 and the original HunyuanVideo. Each is its own graph
/// (none fit the existing single-model i2v/txt2img topologies). Wired from the official ComfyUI templates
/// (video_wan2_2_14B_{i2v,t2v}.json, video_hunyuan_video_1.5_720p_t2v.json, hunyuan_video_text_to_video.json).
/// The MoE classes load a SECOND expert from the <c>unet_low</c> param (literal filename); its requirement is linked
/// in the config's <c>extra</c> for presence-gating. All gate to 24GB via the config's min_vram_mb. Smoke-test live.
/// </summary>
internal static class Vid
{
    /// <summary>Two-stage MoE sampling over a typed <see cref="ComfyWorkflowGraph"/>: high-noise expert for
    /// [0,boundary), low-noise for [boundary,end). The two experts guide at DIFFERENT scales for t2v (high 4.0, low 3.0),
    /// so each stage takes its own cfg (<c>cfg_high</c>/<c>cfg_low</c>); <c>boundary</c> is the reference's sigma
    /// switch-threshold pre-mapped to a step index. <paramref name="sampler"/>/<paramref name="scheduler"/> are the
    /// ALREADY-MAPPED ComfyUI names; <paramref name="refinerSteps"/> is the optional draft/commit knob (null = the legacy
    /// shared-schedule tail). Returns the final latent.</summary>
    public static Output<Slot.Latent> MoESample(ComfyWorkflowGraph g, Output<Slot.Model> modelHigh, Output<Slot.Model> modelLow,
        Output<Slot.Conditioning> positive, Output<Slot.Conditioning> negative, Output<Slot.Latent> latent,
        int steps, int boundary, double cfgHigh, double cfgLow, string sampler, string scheduler, int? refinerSteps, long seed)
    {
        g[VidNodes.HighSampler] = new KSamplerAdvanced
        {
            AddNoise = ComfyWidgets.Toggle.Enable,
            NoiseSeed = seed,
            Steps = steps,
            Cfg = cfgHigh,
            SamplerName = sampler,
            Scheduler = scheduler,
            StartAtStep = 0,
            EndAtStep = boundary,
            ReturnWithLeftoverNoise = ComfyWidgets.Toggle.Enable,
            Model = modelHigh,
            Positive = positive,
            Negative = negative,
            LatentImage = latent,
        };
        // refiner_steps: run the low-noise stage on its OWN schedule with exactly this many steps, leaving the high-noise
        // structure phase untouched — a draft (small N) then commits (large N) with byte-identical motion, because both
        // runs share the same stage-1 schedule/seed and hand off the same latent. The handoff sits at t* = 1 -
        // boundary/steps; total2 = round(N/t*) puts the refiner's start index on that same sigma (exact whenever N/t* is
        // whole). 0 = decode the handoff as-is; absent/negative = the legacy shared-schedule tail.
        int refiner = refinerSteps ?? -1;
        if (refiner == 0)
        {
            return KSamplerAdvanced.Out(VidNodes.HighSampler);
        }

        int steps2 = steps, start2 = boundary;
        if (refiner > 0)
        {
            double tStar = 1.0 - ((double)boundary / steps);
            steps2 = Math.Max(refiner + 1, (int)Math.Round(refiner / tStar));
            start2 = steps2 - refiner;
        }

        g[VidNodes.LowSampler] = new KSamplerAdvanced
        {
            AddNoise = ComfyWidgets.Toggle.Disable,
            NoiseSeed = seed,
            Steps = steps2,
            Cfg = cfgLow,
            SamplerName = sampler,
            Scheduler = scheduler,
            StartAtStep = start2,
            EndAtStep = 10000,
            ReturnWithLeftoverNoise = ComfyWidgets.Toggle.Disable,
            Model = modelLow,
            Positive = positive,
            Negative = negative,
            LatentImage = KSamplerAdvanced.Out(VidNodes.HighSampler),
        };
        return KSamplerAdvanced.Out(VidNodes.LowSampler);
    }

    /// <summary>Load a high+low expert pair, each through ModelSamplingSD3(shift), over a typed graph. High file = the
    /// resolved checkpoint, low = the resolved <c>unet_low</c>. Both load through
    /// <see cref="ComfyGraph.DiffusionLoaderNode"/>, which picks its node from the bound file.</summary>
    public static (Output<Slot.Model> high, Output<Slot.Model> low) LoadExperts(ComfyWorkflowGraph g, string highFile, string lowFile, double shift)
    {
        g[VidNodes.HighExpert] = ComfyGraph.DiffusionLoaderNode(highFile);
        g[VidNodes.HighSampling] = new ModelSamplingSD3 { Model = UNETLoader.ModelOut(VidNodes.HighExpert), Shift = shift };
        g[VidNodes.LowExpert] = ComfyGraph.DiffusionLoaderNode(lowFile);
        g[VidNodes.LowSampling] = new ModelSamplingSD3 { Model = UNETLoader.ModelOut(VidNodes.LowExpert), Shift = shift };
        return (ModelSamplingSD3.Out(VidNodes.HighSampling), ModelSamplingSD3.Out(VidNodes.LowSampling));
    }
}

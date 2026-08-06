using ImageGen.Comfy;

namespace ImageGen.Comfy.Generation.MageFlow;

/// <summary>Mage-Flow (RL-aligned) text-to-image — full CFG (cfg 5, negatives supported), ~20 steps.</summary>
public sealed class MageFlowWorkflow : MageFlowGenBase { public override string Name => "mage-flow"; }

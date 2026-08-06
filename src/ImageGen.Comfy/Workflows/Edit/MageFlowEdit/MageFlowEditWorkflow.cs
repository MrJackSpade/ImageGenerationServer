using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.MageFlowEdit;

/// <summary>Mage-Flow-Edit (RL-aligned) — full CFG (cfg 5, negatives supported), ~30 steps.</summary>
public sealed class MageFlowEditWorkflow : MageFlowEditBase { public override string Name => "mage-flow-edit"; }

using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.MageFlowEditTurbo;

/// <summary>Mage-Flow-Edit-Turbo — 4-step distilled, cfg 1 (no negative).</summary>
public sealed class MageFlowEditTurboWorkflow : MageFlowEditBase { public override string Name => "mage-flow-edit-turbo"; }

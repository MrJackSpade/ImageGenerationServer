using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>ComfyUI output-slot kinds — phantom markers (never instantiated) that type an <see cref="Output{TSlot}"/>
/// edge, so a MODEL wire cannot be plugged into a CLIP socket at compile time.</summary>
public static class Slot
{
    public sealed class Model { private Model() { } }
    public sealed class Clip { private Clip() { } }
    public sealed class Vae { private Vae() { } }
    public sealed class Conditioning { private Conditioning() { } }
    public sealed class Latent { private Latent() { } }
    public sealed class Image { private Image() { } }
    public sealed class Video { private Video() { } }
    public sealed class Float { private Float() { } }
    public sealed class UpscaleModel { private UpscaleModel() { } }
    public sealed class Mask { private Mask() { } }
    public sealed class Int { private Int() { } }
    public sealed class Sampler { private Sampler() { } }
    public sealed class Sigmas { private Sigmas() { } }
    public sealed class Guider { private Guider() { } }
    public sealed class Noise { private Noise() { } }
    public sealed class ControlNet { private ControlNet() { } }
    public sealed class ClipVision { private ClipVision() { } }
}

/// <summary>A typed reference to another node's output — ComfyUI's <c>[nodeId, outputIndex]</c> edge. The
/// <typeparamref name="TSlot"/> marks WHICH kind of output it is, so a graph is wired type-safely. Serializes as the
/// two-element array ComfyUI expects; never deserialized (an emitted graph is write-only).</summary>
[JsonConverter(typeof(OutputConverterFactory))]
public readonly record struct Output<TSlot>(string NodeId, int Index);

/// <summary>Serializes any <see cref="Output{TSlot}"/> as <c>["nodeId", index]</c> — byte-identical to the old
/// <c>object[] { nodeId, idx }</c>. Write-only.</summary>
internal sealed class OutputConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Output<>);

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
        Activator.CreateInstance(typeof(Conv<>).MakeGenericType(t.GetGenericArguments()[0])) as JsonConverter
        ?? throw new InvalidOperationException($"Could not create an Output edge converter for {t}.");

    private sealed class Conv<TSlot> : JsonConverter<Output<TSlot>>
    {
        public override Output<TSlot> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
            throw new NotSupportedException("A ComfyUI graph is write-only; an Output edge is never deserialized.");

        public override void Write(Utf8JsonWriter writer, Output<TSlot> value, JsonSerializerOptions o)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(value.NodeId);
            writer.WriteNumberValue(value.Index);
            writer.WriteEndArray();
        }
    }
}

/// <summary>One ComfyUI node: its class type and its typed inputs. A concrete record per node class declares its inputs
/// as <c>required</c> typed properties (<see cref="Output{TSlot}"/> for wired inputs, literals for constants), so a
/// graph cannot omit a required input or wire the wrong output kind. <see cref="ClassType"/> is emitted as the node's
/// <c>class_type</c>; the record's properties become its <c>inputs</c> object, in declaration order.</summary>
public abstract record ComfyNode
{
    /// <summary>The ComfyUI <c>class_type</c> this node emits. Internal so it is never serialized as one of the node's
    /// inputs (STJ writes only public members); the graph converter reads it to build the node's envelope.</summary>
    internal abstract string ClassType { get; }
}

/// <summary>The ComfyUI API-format prompt graph: node id → typed node, in insertion order. It stays typed all the way
/// to the wire — <see cref="ComfyWorkflowGraphConverter"/> renders it to the exact <c>{ id: { class_type, inputs } }</c>
/// JSON <c>/prompt</c> expects, so no <c>Dictionary&lt;string, object&gt;</c> stands between a workflow and the socket.</summary>
[JsonConverter(typeof(ComfyWorkflowGraphConverter))]
public sealed class ComfyWorkflowGraph
{
    private readonly Dictionary<string, ComfyNode> _nodes;

    public ComfyWorkflowGraph() => _nodes = [];

    /// <summary>Place a typed node at a graph-local id (the id is the ComfyUI node key, preserved exactly). Only a
    /// <see cref="ComfyNode"/> can enter a graph — there is no untyped path in.</summary>
    public ComfyNode this[string id]
    {
        get => _nodes[id];
        set => _nodes[id] = value;
    }

    /// <summary>The node map by id, for tests that assert on the emitted node shapes.</summary>
    public IReadOnlyDictionary<string, ComfyNode> Raw => _nodes;

    public int Count => _nodes.Count;

    internal IReadOnlyDictionary<string, ComfyNode> Nodes => _nodes;
}

/// <summary>Renders a <see cref="ComfyWorkflowGraph"/> to ComfyUI's wire JSON. Each typed <see cref="ComfyNode"/> becomes
/// <c>{ "class_type": …, "inputs": { …its typed properties… } }</c>. Write-only — a graph is emitted, never read back.</summary>
internal sealed class ComfyWorkflowGraphConverter : JsonConverter<ComfyWorkflowGraph>
{
    /// <summary>Plain options for rendering a node's inputs. The <see cref="Output{TSlot}"/> edges carry their own
    /// converter by attribute, so nothing needs registering here and there is no converter to recurse into.</summary>
    private static readonly JsonSerializerOptions NodeInputs = new();

    public override ComfyWorkflowGraph Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
        throw new NotSupportedException("A ComfyUI graph is write-only; it is never deserialized.");

    public override void Write(Utf8JsonWriter writer, ComfyWorkflowGraph graph, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, ComfyNode> entry in graph.Nodes)
        {
            ComfyNode node = entry.Value;
            writer.WritePropertyName(entry.Key);
            writer.WriteStartObject();
            writer.WriteString(ComfyGraphKeys.ClassType, node.ClassType);
            writer.WritePropertyName(ComfyGraphKeys.Inputs);
            JsonSerializer.Serialize(writer, node, node.GetType(), NodeInputs);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}

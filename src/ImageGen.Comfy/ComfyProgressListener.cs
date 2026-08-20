using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ImageGen.Application.Rendering;
using Microsoft.Extensions.Hosting;

namespace ImageGen.Comfy;

/// <summary>
/// Keeps a server-side connection to ComfyUI's live-progress WebSocket and reports each progress frame's step fraction
/// to <see cref="IStepProgressSink"/> (the orchestrator), keyed by backend prompt id. This is what lets the job/queue
/// views serve REAL sampler-step progress for the render on the GPU — the browser-facing /ws proxy is per-user and
/// filtered, so a cross-user surface (the queue page) can't draw from it; this listener is the one unfiltered observer.
/// </summary>
public sealed class ComfyProgressListener(
    IComfyClient comfy,
    IStepProgressSink sink,
    IRenderProgressPublisher events,
    ILogger<ComfyProgressListener> log)
    : BackgroundService
{
    /// <summary>How long to wait before re-dialing the progress socket after it drops or refuses — keeps a down/restarting
    /// backend from being hammered in a hot loop. Matches the UI's 2s poll cadence.</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    /// <summary>ComfyUI /ws frame type discriminators and JSON field names.</summary>
    private static class Frame
    {
        public const string Type = "type";
        public const string Data = "data";
        public const string PromptId = "prompt_id";
        public const string Progress = "progress";
        public const string ProgressState = "progress_state";
        public const string Value = "value";
        public const string Max = "max";
        public const string Nodes = "nodes";
        public const string State = "state";
        public const string Running = "running";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using WebSocket ws = await comfy.ConnectProgressSocketAsync(stoppingToken);
                await PumpAsync(ws, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A dead socket only means live step progress pauses until the redial below succeeds — the render
                // itself is unaffected — so log and reconnect rather than letting the host crash over a progress feed.
                log.LogWarning(ex, "ComfyUI progress socket dropped; reconnecting.");
            }

            await Task.Delay(ReconnectDelay, stoppingToken);
        }
    }

    private async Task PumpAsync(WebSocket ws, CancellationToken ct)
    {
        byte[] buf = new byte[64 * 1024];
        using MemoryStream message = new();
        string? currentPromptId = null;
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            message.Write(buf, 0, r.Count);
            if (!r.EndOfMessage)
            {
                continue;
            }

            if (r.MessageType == WebSocketMessageType.Text)
            {
                string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                string? promptId = Observe(text);
                if (!string.IsNullOrEmpty(promptId))
                {
                    currentPromptId = promptId;
                }

                events.PublishText(text, promptId);
            }
            else if (r.MessageType == WebSocketMessageType.Binary)
            {
                // Legacy ComfyUI preview frames carry no prompt id. Its progress hook sends the prompt-bearing text
                // frame immediately before the binary image on this one ordered socket, so associate it with that
                // current prompt. The publisher resolves ownership and withholds it if the route is no longer live.
                events.PublishBinary(message.GetBuffer().AsMemory(0, (int)message.Length), currentPromptId);
            }

            message.SetLength(0);
        }
    }

    /// <summary>Parse one text frame and report its step fraction, if it carries one. Two frame shapes carry progress:
    /// <c>progress</c> (flat value/max) and <c>progress_state</c> (per-node values; the RUNNING node's is the live
    /// one). Anything else — status frames, execution events, non-JSON — carries no fraction and is dropped.</summary>
    private string? Observe(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;   // not JSON — ComfyUI's own free-form frames carry no prompt id or step data
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty(Frame.Type, out JsonElement type) || type.ValueKind != JsonValueKind.String
                || !doc.RootElement.TryGetProperty(Frame.Data, out JsonElement data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty(Frame.PromptId, out JsonElement pid) || pid.ValueKind != JsonValueKind.String
                || pid.GetString() is not { Length: > 0 } promptId)
            {
                return null;
            }

            double? fraction = type.GetString() switch
            {
                Frame.Progress => FractionOf(data),
                Frame.ProgressState => RunningNodeFractionOf(data),
                _ => null,
            };
            if (fraction is { } f)
            {
                sink.ReportStepFraction(promptId, f);
            }

            return promptId;
        }
    }

    /// <summary>value/max of one progress object, clamped to 0..1; null when the fields are absent or max is 0.</summary>
    private static double? FractionOf(JsonElement obj)
    {
        if (!obj.TryGetProperty(Frame.Value, out JsonElement value) || !value.TryGetDouble(out double v)
            || !obj.TryGetProperty(Frame.Max, out JsonElement max) || !max.TryGetDouble(out double m) || m <= 0)
        {
            return null;
        }

        return Math.Clamp(v / m, 0, 1);
    }

    /// <summary>The fraction of the node a progress_state frame reports as running, or null when none is.</summary>
    private static double? RunningNodeFractionOf(JsonElement data)
    {
        if (!data.TryGetProperty(Frame.Nodes, out JsonElement nodes) || nodes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty node in nodes.EnumerateObject())
        {
            if (node.Value.ValueKind == JsonValueKind.Object
                && node.Value.TryGetProperty(Frame.State, out JsonElement state)
                && state.ValueKind == JsonValueKind.String
                && state.ValueEquals(Frame.Running)
                && FractionOf(node.Value) is { } f)
            {
                return f;
            }
        }

        return null;
    }
}

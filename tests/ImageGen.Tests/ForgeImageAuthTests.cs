using ImageGen.Api.Endpoints;
using ImageGen.Application.Images;
using ImageGen.Application.Media;
using ImageGen.Application.Platform;
using ImageGen.Application.Rendering;
using ImageGen.Domain.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Reflection;

namespace ImageGen.Tests;

/// <summary>
/// The image routes check visibility BEFORE they touch their caches. A thumbnail or clip that some earlier owner
/// warmed into the process cache is still that owner's picture, so a non-owner who asks for the same id must be
/// refused — priming the cache must not become a side door around the owner check.
/// </summary>
public sealed class ForgeImageAuthTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>The key <c>/forge/image/{id}?w=&amp;still=true</c> caches its thumbnail under. Reproduced here so the
    /// test can prime the cache the handler would read from.</summary>
    private static string ThumbKey(string id, int width) => $"thumb:{id}:{width}:s";

    [Fact]
    public async Task A_primed_thumbnail_cache_still_refuses_a_non_owner()
    {
        const string id = "someone-elses-image";
        const int width = 256;
        byte[] secret = [1, 2, 3, 4];

        await using ImageEndpointHost host = await ImageEndpointHost.StartAsync(deny: true, prime: cache =>
            _ = cache.Set(ThumbKey(id, width), new MediaPayload(secret, "image/png")));

        HttpResponseMessage response = await host.Client.GetAsync($"/forge/image/{id}?w={width}&still=true", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        byte[] body = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.NotEqual(secret, body);   // the primed bytes were never served
    }

    /// <summary>The owner is served the cached thumbnail — the check gates the cache, it does not disable it.</summary>
    [Fact]
    public async Task A_primed_thumbnail_cache_serves_the_owner()
    {
        const string id = "my-own-image";
        const int width = 256;
        byte[] mine = [9, 8, 7, 6];

        await using ImageEndpointHost host = await ImageEndpointHost.StartAsync(deny: false, prime: cache =>
            _ = cache.Set(ThumbKey(id, width), new MediaPayload(mine, "image/png")));

        HttpResponseMessage response = await host.Client.GetAsync($"/forge/image/{id}?w={width}&still=true", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mine, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>
    /// A real minimal-API host with only the /forge image routes mounted, one authenticated owner stamped onto every
    /// request, and a visibility repository that answers a fixed yes/no. The byte-store, ComfyUI, and media services
    /// are throwing proxies: a refusal happens before any of them is reached, so being called at all is a failure.
    /// </summary>
    private sealed class ImageEndpointHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public HttpClient Client { get; }

        private ImageEndpointHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public static async Task<ImageEndpointHost> StartAsync(bool deny, Action<IMemoryCache> prime)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
            _ = builder.Services.AddRouting();
            _ = builder.Services.AddMemoryCache();
            _ = builder.Services.AddSingleton<IUploadStore, InMemoryUploadStore>();
            _ = builder.Services.AddSingleton<IImageVisibilityRepository>(new FixedVisibility(readable: !deny));
            _ = builder.Services.AddScoped<ImageVisibilityService>();
            // The image family's other routes are mapped too; register their services as throwing proxies so their
            // delegates bind (a call would fault) while the thumbnail path under test never reaches one.
            _ = builder.Services.AddSingleton(ThrowingProxy<IImageBlobRepository>.Create());
            _ = builder.Services.AddSingleton(ThrowingProxy<IComfyClient>.Create());
            _ = builder.Services.AddSingleton(ThrowingProxy<IMediaProcessor>.Create());
            _ = builder.Services.AddSingleton(ThrowingProxy<IImageFrameRepository>.Create());
            _ = builder.Services.AddSingleton(ThrowingProxy<IJobRepository>.Create());
            _ = builder.Services.AddSingleton(ThrowingProxy<ILoraPreviewRepository>.Create());
            _ = builder.Services.AddSingleton(new SubmissionMemoryGate(ThrowingProxy<ISystemMemory>.Create(), () => 0));

            WebApplication app = builder.Build();
            prime(app.Services.GetRequiredService<IMemoryCache>());

            // Stamp the owner the way the real /forge auth filter does, so OwnerOf resolves.
            _ = app.Use(async (ctx, next) =>
            {
                ctx.Items["ForgeOwnerUserId"] = 1L;
                await next(ctx);
            });
            ForgeApi.MapImages(app.MapGroup(ForgeApi.Origin.PublicBase));

            await app.StartAsync();
            IServerAddressesFeature addresses = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>() ?? throw new InvalidOperationException("no server addresses feature");
            string address = addresses.Addresses.First();
            return new ImageEndpointHost(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    /// <summary>An <see cref="IImageVisibilityRepository"/> that answers the same for every id — the two states the
    /// endpoint has to tell apart.</summary>
    private sealed class FixedVisibility(bool readable) : IImageVisibilityRepository
    {
        public Task<bool> IsReadableAsync(long userId, string imageId, CancellationToken ct) => Task.FromResult(readable);

        public Task<IReadOnlySet<string>> ReadableAsync(
            long userId, IReadOnlyCollection<string> imageIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(
                readable ? imageIds.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>A stub for an interface whose members must never run in this test — every call throws, so reaching one
    /// is an assertion failure rather than a silent pass.</summary>
    private class ThrowingProxy<T> : DispatchProxy where T : class
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"{typeof(T).Name}.{targetMethod?.Name} must not be called before the owner check");

        public static T Create() => DispatchProxy.Create<T, ThrowingProxy<T>>();
    }
}

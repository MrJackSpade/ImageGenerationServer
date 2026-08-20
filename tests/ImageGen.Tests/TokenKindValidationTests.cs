using ImageGen.Api.Contracts;
using ImageGen.Api.Endpoints;
using ImageGen.Application.Services;
using ImageGen.Domain;
using ImageGen.Domain.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;

namespace ImageGen.Tests;

public sealed class TokenKindValidationTests
{
    [Theory]
    [InlineData("tag", TokenKind.Tag)]
    [InlineData("TAG", TokenKind.Tag)]
    [InlineData("artist", TokenKind.Artist)]
    [InlineData("Artist", TokenKind.Artist)]
    public void Only_explicit_tag_and_artist_wire_values_parse(string wire, TokenKind expected) =>
        Assert.Equal(expected, TokenKindWire.Parse(wire));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other")]
    public void Unknown_wire_values_are_not_reclassified_as_tags(string? wire) =>
        _ = Assert.Throws<FormatException>(() => TokenKindWire.Parse(wire));

    [Fact]
    public void An_invalid_enum_value_is_not_serialized_as_a_tag() =>
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => ((TokenKind)99).ToWire());

    [Fact]
    public void A_corrupt_marks_map_kind_surfaces_during_mapping() =>
        _ = Assert.Throws<FormatException>(
            () => WireMapping.MarksFromMap(new Dictionary<string, string> { ["token"] = "corrupt" }));

    [Fact]
    public async Task Invalid_bookmark_and_ban_kinds_return_400_before_any_repository_call()
    {
        await using TokenEndpointHost host = await TokenEndpointHost.StartAsync();
        Func<HttpRequestMessage>[] requests =
        [
            () => Post("/api/bookmarks/tokens", new { name = "x", kind = "wrong" }),
            () => new HttpRequestMessage(HttpMethod.Delete, "/api/bookmarks/tokens?name=x&kind=wrong"),
            () => Post("/api/bookmarks/tokens/pin", new { name = "x", kind = "wrong", pinned = true }),
            () => new HttpRequestMessage(HttpMethod.Get, "/api/bookmarks/categories?scope=token&name=x&kind=wrong"),
            () => Post("/api/bookmarks/tokens/categories", new { name = "x", kind = "wrong", categories = Array.Empty<string>() }),
            () => Post("/api/bans", new { modelId = "wf", name = "x", kind = "wrong" }),
            () => new HttpRequestMessage(HttpMethod.Delete, "/api/bans?modelId=wf&name=x&kind=wrong"),
        ];

        foreach (Func<HttpRequestMessage> create in requests)
        {
            using HttpRequestMessage request = create();
            using HttpResponseMessage response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    private static HttpRequestMessage Post(string path, object body) => new(HttpMethod.Post, path)
    {
        Content = JsonContent.Create(body),
    };

    private sealed class TokenEndpointHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public HttpClient Client { get; }

        private TokenEndpointHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public static async Task<TokenEndpointHost> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
            _ = builder.Services.AddRouting();
            _ = builder.Services.AddSingleton<IBookmarkRepository>(ThrowingProxy<IBookmarkRepository>.Create());
            _ = builder.Services.AddSingleton<IBannedTokenRepository>(ThrowingProxy<IBannedTokenRepository>.Create());
            _ = builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            _ = builder.Services.AddScoped<BookmarkService>();
            _ = builder.Services.AddScoped<BanService>();

            WebApplication app = builder.Build();
            _ = app.Use(async (context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "7")], authenticationType: "test"));
                await next(context);
            });
            RouteGroupBuilder api = app.MapGroup("/api");
            api.MapBookmarkEndpoints();
            api.MapBanEndpoints();

            await app.StartAsync();
            IServerAddressesFeature addresses = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>() ?? throw new InvalidOperationException("no server addresses feature");
            string address = addresses.Addresses.First();
            return new TokenEndpointHost(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private class ThrowingProxy<T> : DispatchProxy where T : class
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"{typeof(T).Name}.{targetMethod?.Name} must not be called for an invalid token kind");

        public static T Create() => DispatchProxy.Create<T, ThrowingProxy<T>>();
    }
}

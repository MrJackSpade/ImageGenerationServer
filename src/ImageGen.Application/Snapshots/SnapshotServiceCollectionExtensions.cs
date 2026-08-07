using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ImageGen.Application.Snapshots;

/// <summary>
/// DI registration for the snapshot cache. Each <see cref="AddSnapshot{T}"/> registers one source as a singleton bound
/// to three service types sharing the one instance: <see cref="ISnapshot{T}"/> (the injected read surface),
/// <see cref="SnapshotEntry{T}"/> (the concrete type, for triggers that hold the source directly), and
/// <see cref="SnapshotEntry"/> (the non-generic collection the <see cref="SnapshotSyncWorker"/> resolves via
/// <c>IEnumerable</c>). The Web host adds the hosted-service adapter that runs <see cref="SnapshotSyncWorker.RunAsync"/>.
/// </summary>
public static class SnapshotServiceCollectionExtensions
{
    /// <summary>
    /// Register one snapshot source. The loader is resolved lazily against the root <see cref="IServiceProvider"/>, so
    /// it may capture singleton collaborators (repositories, the Comfy client) directly.
    /// </summary>
    /// <typeparam name="T">The cached value type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="loader">Produces the cached value; runs on the single sync worker.</param>
    /// <param name="options">Per-source options (the backstop interval).</param>
    public static IServiceCollection AddSnapshot<T>(
        this IServiceCollection services,
        Func<IServiceProvider, CancellationToken, Task<T>> loader,
        SnapshotOptions options)
    {
        _ = Domain.Ensure.NotNull(loader);
        _ = Domain.Ensure.NotNull(options);

        _ = services.AddSingleton(sp =>
            new SnapshotEntry<T>(typeof(T).Name, options.BackstopInterval, ct => loader(sp, ct)));
        _ = services.AddSingleton<ISnapshot<T>>(sp => sp.GetRequiredService<SnapshotEntry<T>>());
        _ = services.AddSingleton<SnapshotEntry>(sp => sp.GetRequiredService<SnapshotEntry<T>>());

        services.TryAddSingleton<SnapshotSyncWorker>();
        return services;
    }
}

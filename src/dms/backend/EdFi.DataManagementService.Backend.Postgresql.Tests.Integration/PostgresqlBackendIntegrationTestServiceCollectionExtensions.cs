// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal static class PostgresqlBackendIntegrationTestServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresqlBackendIntegrationTestServices(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.TryAddSingleton(new DeadlockRetrySettings());
        services.AddSelectedDataStoreIntegrationTestProvider();
        services.TryAddSingleton<NpgsqlDataSourceCache>();
        services.TryAddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddPostgresqlReferenceResolver();
        services.TryAddSingleton<IDocumentCacheProjectionSupervisor, StubDocumentCacheProjectionSupervisor>();
        services.TryAddSingleton<IDocumentCacheTargetRegistry, StubDocumentCacheTargetRegistry>();

        return services;
    }

    private sealed class StubDocumentCacheProjectionSupervisor : IDocumentCacheProjectionSupervisor
    {
        public System.Collections.Immutable.ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts =>
            [];

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Stub supervisor is unused in integration DI tests.");
    }

    private sealed class StubDocumentCacheTargetRegistry : IDocumentCacheTargetRegistry
    {
        private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } = new([], ObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } = new([], ObservedAt);

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Stub registry is unused in integration DI tests.");
    }
}

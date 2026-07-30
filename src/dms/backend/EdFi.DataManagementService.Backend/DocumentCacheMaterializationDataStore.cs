// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheMaterializationDataStore
{
    SqlDialect Dialect { get; }

    Task<TResult> ExecuteReaderAsync<TResult>(
        DocumentCacheMaterializationRequest request,
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    );

    Task<HydratedPage> HydrateAsync(
        DocumentCacheMaterializationRequest request,
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken cancellationToken = default
    );
}

internal sealed class AmbientDocumentCacheMaterializationDataStore(
    IRelationalCommandExecutor commandExecutor,
    IDocumentHydrator documentHydrator
) : IDocumentCacheMaterializationDataStore
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IDocumentHydrator _documentHydrator =
        documentHydrator ?? throw new ArgumentNullException(nameof(documentHydrator));

    public AmbientDocumentCacheMaterializationDataStore(IRelationalCommandExecutor commandExecutor)
        : this(commandExecutor, new ThrowingDocumentHydrator()) { }

    public SqlDialect Dialect => _commandExecutor.Dialect;

    public Task<TResult> ExecuteReaderAsync<TResult>(
        DocumentCacheMaterializationRequest request,
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        DocumentCacheMaterializationDataStoreGuards.RequireValidatedTargetContext(request, Dialect);

        return _commandExecutor.ExecuteReaderAsync(command, readAsync, cancellationToken);
    }

    public Task<HydratedPage> HydrateAsync(
        DocumentCacheMaterializationRequest request,
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        DocumentCacheMaterializationDataStoreGuards.RequireValidatedTargetContext(request, Dialect);

        return _documentHydrator.HydrateAsync(plan, keyset, executionOptions, cancellationToken);
    }

    private sealed class ThrowingDocumentHydrator : IDocumentHydrator
    {
        public Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        ) =>
            throw new NotSupportedException(
                "This DocumentCache materialization data store was created for command execution only."
            );
    }
}

internal static class DocumentCacheMaterializationDataStoreGuards
{
    public static void RequireValidatedTargetContext(
        DocumentCacheMaterializationRequest request,
        SqlDialect dialect
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (
            request.TargetContext.TargetValidation
            != DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
        )
        {
            throw new InvalidOperationException(
                "DocumentCache materialization requires a target context whose MappingSet was selected "
                    + "for TargetKey.DataStoreId after EffectiveSchema and ResourceKey seed validation."
            );
        }

        if (request.TargetContext.MappingSet.Key.Dialect != dialect)
        {
            throw new InvalidOperationException(
                "DocumentCache materialization target context dialect "
                    + $"'{request.TargetContext.MappingSet.Key.Dialect}' does not match the materialization data store dialect '{dialect}'. "
                    + "Provider adapters require a target context whose MappingSet was selected for TargetKey.DataStoreId "
                    + "after EffectiveSchema and ResourceKey seed validation."
            );
        }
    }
}

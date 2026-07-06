// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend;

internal interface IRelationalWritePersister
{
    Task<RelationalWritePersistResult> PersistAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );

    Task AuthorizeProposedRelationshipAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalWriteNoProfilePersister(
    IRelationalParameterConfigurator? parameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null,
    ILogger<RelationalWriteNoProfilePersister>? logger = null
) : IRelationalWritePersister
{
    private const string AuthorizationResultColumn = "AuthorizationResult";
    private static readonly ConditionalWeakTable<
        ResourceWritePlan,
        string[]
    > _reservedWriteParameterNamesByPlan = new();

    private readonly IRelationalParameterConfigurator _parameterConfigurator =
        parameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;
    private readonly IRelationshipAuthorizationProviderFailureExtractor _relationshipAuthorizationProviderFailureExtractor =
        relationshipAuthorizationProviderFailureExtractor
        ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;
    private readonly ILogger<RelationalWriteNoProfilePersister> _logger =
        logger ?? NullLogger<RelationalWriteNoProfilePersister>.Instance;

    public async Task<RelationalWritePersistResult> PersistAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mergeResult);
        ArgumentNullException.ThrowIfNull(writeSession);
        var targetContext =
            request.TargetContext
            ?? throw new InvalidOperationException(
                "Relational no-profile persistence requires an executor-resolved target context."
            );

        var rootDocumentId = await ResolveRootDocumentIdAsync(
                request.MappingSet,
                request.WritePlan,
                targetContext,
                mergeResult,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds = [];

        await ExecuteDeletesAsync(
                request.MappingSet.Key.Dialect,
                mergeResult,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
        await ExecuteUpsertsAsync(
                request.MappingSet.Key.Dialect,
                mergeResult,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new RelationalWritePersistResult(rootDocumentId, GetTargetDocumentUuid(targetContext));
    }

    public async Task AuthorizeProposedRelationshipAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mergeResult);
        ArgumentNullException.ThrowIfNull(writeSession);

        var relationshipAuthorizationRuntimeCheck =
            mergeResult.ProposedRelationshipAuthorizationRuntimeCheck
            ?? throw new InvalidOperationException(
                "Cannot authorize proposed relationship values without a runtime authorization check."
            );

        try
        {
            await ExecuteProposedRelationshipAuthorizationAsync(
                    writeSession,
                    BuildProposedRelationshipAuthorizationCommand(
                        request.MappingSet,
                        request.WritePlan,
                        mergeResult
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            ThrowMappedRelationshipAuthorizationFailure(
                request.MappingSet.Key.Dialect,
                relationshipAuthorizationRuntimeCheck,
                ex
            );
            throw;
        }
    }

    private static DocumentUuid GetTargetDocumentUuid(RelationalWriteTargetContext targetContext) =>
        targetContext switch
        {
            RelationalWriteTargetContext.CreateNew(var documentUuid) => documentUuid,
            RelationalWriteTargetContext.ExistingDocument(_, var documentUuid, _) => documentUuid,
            _ => throw new ArgumentOutOfRangeException(nameof(targetContext), targetContext, null),
        };

    private static async Task ExecuteDeletesAsync(
        SqlDialect dialect,
        RelationalWriteMergeResult mergeResult,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        foreach (var tableState in mergeResult.TablesInDependencyOrder.Reverse())
        {
            if (RelationalWriteMergeSupport.IsCollectionAlignedExtensionScope(tableState.TableWritePlan))
            {
                await DeleteOmittedCollectionAlignedScopeRowsAsync(
                        dialect,
                        tableState,
                        rootDocumentId,
                        reservedCollectionItemIds,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                continue;
            }

            if (tableState.TableWritePlan.CollectionMergePlan is not null)
            {
                if (
                    tableState.TableWritePlan.TableModel.IdentityMetadata.TableKind
                    is not (DbTableKind.Collection or DbTableKind.ExtensionCollection)
                )
                {
                    continue;
                }

                await DeleteOmittedCollectionRowsAsync(
                        dialect,
                        tableState,
                        rootDocumentId,
                        reservedCollectionItemIds,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                continue;
            }

            await DeleteOmittedNonCollectionRowAsync(
                    tableState,
                    rootDocumentId,
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task ExecuteUpsertsAsync(
        SqlDialect dialect,
        RelationalWriteMergeResult mergeResult,
        long rootDocumentId,
        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<RelationalWriteMergedTableState> pendingTableStates =
            mergeResult.TablesInDependencyOrder;

        while (pendingTableStates.Count > 0)
        {
            List<RelationalWriteMergedTableState> deferredTableStates = new(pendingTableStates.Count);
            var persistedTableCount = 0;

            foreach (var tableState in pendingTableStates)
            {
                if (HasBlockingUnresolvedCollectionItemIds(tableState, reservedCollectionItemIds))
                {
                    deferredTableStates.Add(tableState);
                    continue;
                }

                if (RelationalWriteMergeSupport.IsCollectionAlignedExtensionScope(tableState.TableWritePlan))
                {
                    await UpsertCollectionAlignedScopeRowsAsync(
                            dialect,
                            tableState,
                            rootDocumentId,
                            reservedCollectionItemIds,
                            writeSession,
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                    persistedTableCount++;
                    continue;
                }

                if (tableState.TableWritePlan.CollectionMergePlan is not null)
                {
                    if (
                        tableState.TableWritePlan.TableModel.IdentityMetadata.TableKind
                        is not (DbTableKind.Collection or DbTableKind.ExtensionCollection)
                    )
                    {
                        persistedTableCount++;
                        continue;
                    }

                    await UpsertCollectionRowsAsync(
                            dialect,
                            tableState,
                            rootDocumentId,
                            reservedCollectionItemIds,
                            writeSession,
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                    persistedTableCount++;
                    continue;
                }

                await UpsertNonCollectionRowAsync(
                        tableState,
                        rootDocumentId,
                        reservedCollectionItemIds,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                persistedTableCount++;
            }

            if (deferredTableStates.Count == 0)
            {
                return;
            }

            if (persistedTableCount == 0)
            {
                throw new InvalidOperationException(
                    "Relational write upserts could not resolve collection-id dependencies for tables: "
                        + string.Join(
                            ", ",
                            deferredTableStates.Select(tableState => FormatTable(tableState.TableWritePlan))
                        )
                );
            }

            pendingTableStates = deferredTableStates;
        }
    }

    private static bool HasBlockingUnresolvedCollectionItemIds(
        RelationalWriteMergedTableState tableState,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    )
    {
        var selfReservedBindingIndex = tableState.TableWritePlan.CollectionKeyPreallocationPlan?.BindingIndex;

        foreach (var mergedRow in tableState.MergedRows)
        {
            foreach (
                var (value, bindingIndex) in mergedRow.Values.Select(
                    static (value, bindingIndex) => (value, bindingIndex)
                )
            )
            {
                if (bindingIndex == selfReservedBindingIndex)
                {
                    continue;
                }

                if (
                    value is FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId
                    && !reservedCollectionItemIds.ContainsKey(unresolvedCollectionItemId)
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<long> ResolveRootDocumentIdAsync(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        RelationalWriteTargetContext targetContext,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        return targetContext switch
        {
            RelationalWriteTargetContext.CreateNew(var documentUuid) => await InsertDocumentAsync(
                    mappingSet,
                    writePlan,
                    documentUuid,
                    mergeResult,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false),
            RelationalWriteTargetContext.ExistingDocument(var documentId, _, _) => documentId,
            _ => throw new ArgumentOutOfRangeException(nameof(targetContext), targetContext, null),
        };
    }

    private async Task<long> InsertDocumentAsync(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        DocumentUuid documentUuid,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var resource = writePlan.Model.Resource;
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);
        var command = BuildInsertDocumentCommand(mappingSet.Key.Dialect, documentUuid, resourceKeyId);
        var relationshipAuthorizationRuntimeCheck = mergeResult.ProposedRelationshipAuthorizationRuntimeCheck;

        try
        {
            if (relationshipAuthorizationRuntimeCheck is not null)
            {
                return await ExecuteAuthorizedInsertDocumentAsync(
                        writeSession,
                        BuildAuthorizedInsertDocumentCommand(mappingSet, writePlan, mergeResult, command),
                        resource,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            await using var dbCommand = writeSession.CreateCommand(command);
            var scalarResult = await dbCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return RequireDocumentId(scalarResult, resource);
        }
        catch (DbException ex) when (relationshipAuthorizationRuntimeCheck is not null)
        {
            ThrowMappedRelationshipAuthorizationFailure(
                mappingSet.Key.Dialect,
                relationshipAuthorizationRuntimeCheck,
                ex
            );
            throw;
        }
    }

    private static long RequireDocumentId(object? scalarResult, QualifiedResourceName resource)
    {
        if (scalarResult is null or DBNull)
        {
            throw new InvalidOperationException(
                $"Document insert for resource '{RelationalWriteSupport.FormatResource(resource)}' did not return a DocumentId."
            );
        }

        return Convert.ToInt64(scalarResult, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ExecuteAuthorizedInsertDocumentAsync(
        IRelationalWriteSession writeSession,
        RelationalCommand command,
        QualifiedResourceName resource,
        CancellationToken cancellationToken
    )
    {
        await using var dbCommand = writeSession.CreateCommand(command);
        await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await ReadAndValidateProposedRelationshipAuthorizationResultAsync(reader, cancellationToken)
            .ConfigureAwait(false);

        if (
            !await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)
            || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
        )
        {
            throw new InvalidOperationException(
                $"Document insert for resource '{RelationalWriteSupport.FormatResource(resource)}' did not return a DocumentId."
            );
        }

        return RequireDocumentId(reader.GetValue(0), resource);
    }

    private static async Task ExecuteProposedRelationshipAuthorizationAsync(
        IRelationalWriteSession writeSession,
        RelationalCommand command,
        CancellationToken cancellationToken
    )
    {
        await using var dbCommand = writeSession.CreateCommand(command);
        await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await ReadAndValidateProposedRelationshipAuthorizationResultAsync(reader, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ReadAndValidateProposedRelationshipAuthorizationResultAsync(
        DbDataReader reader,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Proposed relationship authorization did not return an authorization result."
            );
        }

        var authorizationResult = Convert.ToInt32(
            reader.GetValue(reader.GetOrdinal(AuthorizationResultColumn)),
            CultureInfo.InvariantCulture
        );

        if (authorizationResult != 1)
        {
            throw new InvalidOperationException(
                $"Proposed relationship authorization returned unexpected result '{authorizationResult}'."
            );
        }
    }

    private RelationalCommand BuildProposedRelationshipAuthorizationCommand(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        RelationalWriteMergeResult mergeResult
    )
    {
        var proposedAuthorizationCommand = BuildProposedRelationshipAuthorizationCommandParts(
            mappingSet,
            writePlan,
            mergeResult
        );

        return new RelationalCommand(
            proposedAuthorizationCommand.AuthorizationSql,
            proposedAuthorizationCommand.Parameters
        );
    }

    private RelationalCommand BuildAuthorizedInsertDocumentCommand(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        RelationalWriteMergeResult mergeResult,
        RelationalCommand insertDocumentCommand
    )
    {
        var proposedAuthorizationCommand = BuildProposedRelationshipAuthorizationCommandParts(
            mappingSet,
            writePlan,
            mergeResult
        );

        return new RelationalCommand(
            $"{proposedAuthorizationCommand.AuthorizationSql}{Environment.NewLine}{insertDocumentCommand.CommandText}",
            CombineParameters(proposedAuthorizationCommand.Parameters, insertDocumentCommand.Parameters)
        );
    }

    private static IReadOnlyList<RelationalParameter> CombineParameters(
        IReadOnlyList<RelationalParameter> first,
        IReadOnlyList<RelationalParameter> second
    )
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        List<RelationalParameter> combined = new(first.Count + second.Count);
        combined.AddRange(first);
        combined.AddRange(second);

        return combined;
    }

    private ProposedRelationshipAuthorizationCommandParts BuildProposedRelationshipAuthorizationCommandParts(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        RelationalWriteMergeResult mergeResult
    )
    {
        var relationshipAuthorizationRuntimeCheck =
            mergeResult.ProposedRelationshipAuthorizationRuntimeCheck
            ?? throw new InvalidOperationException(
                "Cannot build a proposed authorization command without a runtime authorization check."
            );
        var reservedParameterNames = GetReservedWriteParameterNames(writePlan);
        var sqlPlan = relationshipAuthorizationRuntimeCheck.ExecutableShape is { } executableShape
            ? SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
                mappingSet,
                executableShape,
                relationshipAuthorizationRuntimeCheck.ClaimEducationOrganizationIdParameterization,
                relationshipAuthorizationRuntimeCheck.EmittedAuth1Index,
                reservedParameterNames
            )
            : SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
                mappingSet,
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    relationshipAuthorizationRuntimeCheck.CheckSpecs,
                    relationshipAuthorizationRuntimeCheck.ClaimEducationOrganizationIdParameterization,
                    relationshipAuthorizationRuntimeCheck.EmittedAuth1Index,
                    ReservedParameterNames: reservedParameterNames
                )
            );

        return new ProposedRelationshipAuthorizationCommandParts(
            sqlPlan.AuthorizationSql,
            BuildRelationshipAuthorizationParameters(sqlPlan, relationshipAuthorizationRuntimeCheck)
        );
    }

    private sealed record ProposedRelationshipAuthorizationCommandParts(
        string AuthorizationSql,
        IReadOnlyList<RelationalParameter> Parameters
    );

    private IReadOnlyList<RelationalParameter> BuildRelationshipAuthorizationParameters(
        SingleRecordRelationshipAuthorizationSqlPlan sqlPlan,
        ProposedRelationshipAuthorizationRuntimeCheck relationshipAuthorizationRuntimeCheck
    )
    {
        Dictionary<string, object?> valuesByParameterName = new(
            sqlPlan.ParametersInOrder.Count,
            StringComparer.Ordinal
        );

        AddProposedValueParameterValues(
            valuesByParameterName,
            sqlPlan,
            relationshipAuthorizationRuntimeCheck
        );
        RelationshipAuthorizationCommandParameterBuilder.AddAuthorizationParameterValues(
            valuesByParameterName,
            relationshipAuthorizationRuntimeCheck.ClaimEducationOrganizationIdParameterization
        );

        List<RelationalParameter> parameters = new(sqlPlan.ParametersInOrder.Count);

        foreach (var parameter in sqlPlan.ParametersInOrder)
        {
            parameters.Add(
                RelationshipAuthorizationCommandParameterBuilder.BuildParameter(
                    parameter,
                    valuesByParameterName[parameter.ParameterName],
                    _parameterConfigurator
                )
            );
        }

        return parameters;
    }

    private static void AddProposedValueParameterValues(
        IDictionary<string, object?> valuesByParameterName,
        SingleRecordRelationshipAuthorizationSqlPlan sqlPlan,
        ProposedRelationshipAuthorizationRuntimeCheck relationshipAuthorizationRuntimeCheck
    )
    {
        Dictionary<
            (int StrategyOrdinal, int SubjectOrdinal),
            ProposedRelationshipAuthorizationRuntimeValue
        > valuesByOrdinal = new(CountRuntimeSubjects(relationshipAuthorizationRuntimeCheck.Strategies));

        foreach (var strategy in relationshipAuthorizationRuntimeCheck.Strategies)
        {
            foreach (var subject in strategy.Subjects)
            {
                valuesByOrdinal.Add((strategy.StrategyOrdinal, subject.SubjectOrdinal), subject.RuntimeValue);
            }
        }

        foreach (var proposedValueParameter in sqlPlan.ProposedValueParametersInOrder)
        {
            if (
                !valuesByOrdinal.TryGetValue(
                    (proposedValueParameter.StrategyOrdinal, proposedValueParameter.SubjectOrdinal),
                    out var value
                )
            )
            {
                throw new InvalidOperationException(
                    "Proposed relationship authorization SQL requested a runtime value for "
                        + $"strategy '{proposedValueParameter.StrategyOrdinal}' subject '{proposedValueParameter.SubjectOrdinal}', "
                        + "but no extracted value was available."
                );
            }

            valuesByParameterName[proposedValueParameter.ParameterName] = value switch
            {
                ProposedRelationshipAuthorizationRuntimeValue.SubjectValue subjectValue => subjectValue.Value,
                ProposedRelationshipAuthorizationRuntimeValue.TransitivePeopleFirstHopAnchorValue anchorValue =>
                    anchorValue.Value,
                _ => throw new InvalidOperationException(
                    $"Unsupported proposed relationship authorization runtime value '{value.GetType().Name}'."
                ),
            };
        }
    }

    private static int CountRuntimeSubjects(
        IReadOnlyList<ProposedRelationshipAuthorizationRuntimeStrategy> strategies
    )
    {
        var count = 0;

        foreach (var strategy in strategies)
        {
            count += strategy.Subjects.Count;
        }

        return count;
    }

    private static IReadOnlyList<string> GetReservedWriteParameterNames(ResourceWritePlan writePlan) =>
        _reservedWriteParameterNamesByPlan.GetValue(writePlan, BuildReservedWriteParameterNames);

    private static string[] BuildReservedWriteParameterNames(ResourceWritePlan writePlan)
    {
        var columnBindingCount = 0;

        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            columnBindingCount += tablePlan.ColumnBindings.Length;
        }

        List<string> reservedNames = new(columnBindingCount);
        HashSet<string> seenNames = new(columnBindingCount, StringComparer.OrdinalIgnoreCase);

        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            foreach (var binding in tablePlan.ColumnBindings)
            {
                var parameterName = binding.ParameterName.TrimStart('@');

                if (seenNames.Add(parameterName))
                {
                    reservedNames.Add(parameterName);
                }
            }
        }

        return [.. reservedNames];
    }

    private bool TryMapRelationshipAuthorizationFailure(
        SqlDialect dialect,
        ProposedRelationshipAuthorizationRuntimeCheck relationshipAuthorizationRuntimeCheck,
        DbException exception,
        out RelationshipAuthorizationFailure? relationshipFailure,
        out RelationshipAuthorizationProviderFailureDiagnostic? invalidFailureDiagnostic
    ) =>
        RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
            dialect,
            exception,
            _relationshipAuthorizationProviderFailureExtractor,
            relationshipAuthorizationRuntimeCheck.EmittedAuth1Index,
            relationshipAuthorizationRuntimeCheck.CheckSpecs,
            relationshipAuthorizationRuntimeCheck
                .ClaimEducationOrganizationIdParameterization
                .ClaimEducationOrganizationIds,
            out relationshipFailure,
            out invalidFailureDiagnostic
        );

    [DoesNotReturn]
    private void ThrowMappedRelationshipAuthorizationFailure(
        SqlDialect dialect,
        ProposedRelationshipAuthorizationRuntimeCheck relationshipAuthorizationRuntimeCheck,
        DbException exception
    )
    {
        if (
            TryMapRelationshipAuthorizationFailure(
                dialect,
                relationshipAuthorizationRuntimeCheck,
                exception,
                out var relationshipFailure,
                out var invalidFailureDiagnostic
            )
        )
        {
            throw new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure!);
        }

        if (invalidFailureDiagnostic is not null)
        {
            RelationshipAuthorizationProviderFailureMapper.LogInvalidFailurePayload(
                _logger,
                invalidFailureDiagnostic
            );

            throw new RelationalWriteInvalidRelationshipAuthorizationFailureException(
                RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError,
                AuthorizationSecurityConfigurationDiagnostics.ForRelationshipAuthorizationAuth1(
                    invalidFailureDiagnostic,
                    relationshipAuthorizationRuntimeCheck.CheckSpecs
                )
            );
        }

        ExceptionDispatchInfo.Capture(exception).Throw();
        throw new InvalidOperationException("Unreachable relationship authorization failure mapping state.");
    }

    private static RelationalCommand BuildInsertDocumentCommand(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                VALUES (@documentUuid, @resourceKeyId)
                RETURNING "DocumentId";
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                ]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
                VALUES (@documentUuid, @resourceKeyId);
                SELECT SCOPE_IDENTITY();
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                ]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }

    private static async Task DeleteOmittedNonCollectionRowAsync(
        RelationalWriteMergedTableState tableState,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var currentRow = GetSingleRowOrThrow(tableState.CurrentRows, "current", tableState.TableWritePlan);
        var mergedRow = GetSingleRowOrThrow(tableState.MergedRows, "merged", tableState.TableWritePlan);

        if (currentRow is null || mergedRow is not null)
        {
            return;
        }

        if (tableState.TableWritePlan.DeleteByParentSql is null)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableState.TableWritePlan)}' cannot delete an omitted scope because no DeleteByParentSql was compiled."
            );
        }

        await ExecuteNonQueryAsync(
                writeSession,
                BuildRowCommand(
                    tableState.TableWritePlan,
                    tableState.TableWritePlan.DeleteByParentSql,
                    currentRow,
                    rootDocumentId,
                    reservedCollectionItemIds
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task UpsertNonCollectionRowAsync(
        RelationalWriteMergedTableState tableState,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var currentRow = GetSingleRowOrThrow(tableState.CurrentRows, "current", tableState.TableWritePlan);
        var mergedRow = GetSingleRowOrThrow(tableState.MergedRows, "merged", tableState.TableWritePlan);

        if (mergedRow is null)
        {
            return;
        }

        if (currentRow is null)
        {
            await ExecuteNonQueryAsync(
                    writeSession,
                    BuildRowCommand(
                        tableState.TableWritePlan,
                        tableState.TableWritePlan.InsertSql,
                        mergedRow,
                        rootDocumentId,
                        reservedCollectionItemIds
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return;
        }

        if (currentRow.Values.SequenceEqual(mergedRow.Values))
        {
            return;
        }

        if (tableState.TableWritePlan.UpdateSql is null)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableState.TableWritePlan)}' requires UpdateSql to persist a changed non-collection row."
            );
        }

        await ExecuteNonQueryAsync(
                writeSession,
                BuildRowCommand(
                    tableState.TableWritePlan,
                    tableState.TableWritePlan.UpdateSql,
                    mergedRow,
                    rootDocumentId,
                    reservedCollectionItemIds
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task DeleteOmittedCollectionAlignedScopeRowsAsync(
        SqlDialect dialect,
        RelationalWriteMergedTableState tableState,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var mergedRowsByPhysicalIdentity = GetRowsByPhysicalIdentityOrThrow(
            tableState.MergedRows,
            "merged",
            tableState.TableWritePlan
        );

        if (tableState.TableWritePlan.DeleteByParentSql is null)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableState.TableWritePlan)}' cannot delete an omitted aligned scope because no DeleteByParentSql was compiled."
            );
        }

        List<RelationalWriteMergedTableRow> rowsToDelete = new(tableState.CurrentRows.Length);

        foreach (var currentRow in tableState.CurrentRows)
        {
            var physicalIdentity = ResolvePhysicalRowIdentityKey(tableState.TableWritePlan, currentRow);

            if (mergedRowsByPhysicalIdentity.ContainsKey(physicalIdentity))
            {
                continue;
            }

            rowsToDelete.Add(currentRow);
        }

        await ExecuteParameterizedBatchesAsync(
                dialect,
                tableState.TableWritePlan,
                tableState.TableWritePlan.DeleteByParentSql,
                (batchSqlEmitter, rowCount) =>
                    batchSqlEmitter.EmitDeleteByParentBatch(tableState.TableWritePlan, rowCount),
                rowsToDelete,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task UpsertCollectionAlignedScopeRowsAsync(
        SqlDialect dialect,
        RelationalWriteMergedTableState tableState,
        long rootDocumentId,
        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var currentRowsByPhysicalIdentity = GetRowsByPhysicalIdentityOrThrow(
            tableState.CurrentRows,
            "current",
            tableState.TableWritePlan
        );
        List<RelationalWriteMergedTableRow> rowsToUpdate = new(tableState.MergedRows.Length);
        List<RelationalWriteMergedTableRow> rowsToInsert = new(tableState.MergedRows.Length);

        foreach (var mergedRow in tableState.MergedRows)
        {
            var physicalIdentity = ResolvePhysicalRowIdentityKey(tableState.TableWritePlan, mergedRow);

            if (!currentRowsByPhysicalIdentity.TryGetValue(physicalIdentity, out var currentRow))
            {
                rowsToInsert.Add(mergedRow);
                continue;
            }

            if (currentRow.Values.SequenceEqual(mergedRow.Values))
            {
                continue;
            }

            if (tableState.TableWritePlan.UpdateSql is null)
            {
                throw new InvalidOperationException(
                    $"Table '{FormatTable(tableState.TableWritePlan)}' requires UpdateSql to persist a changed aligned scope row."
                );
            }

            rowsToUpdate.Add(mergedRow);
        }

        await ExecuteParameterizedBatchesAsync(
                dialect,
                tableState.TableWritePlan,
                tableState.TableWritePlan.UpdateSql!,
                (batchSqlEmitter, rowCount) =>
                    batchSqlEmitter.EmitUpdateBatch(tableState.TableWritePlan, rowCount),
                rowsToUpdate,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (rowsToInsert.Count == 0)
        {
            return;
        }

        var batchSqlEmitter = new WritePlanBatchSqlEmitter(dialect);

        for (
            var batchStart = 0;
            batchStart < rowsToInsert.Count;
            batchStart += tableState.TableWritePlan.BulkInsertBatching.MaxRowsPerBatch
        )
        {
            var batchCount = Math.Min(
                tableState.TableWritePlan.BulkInsertBatching.MaxRowsPerBatch,
                rowsToInsert.Count - batchStart
            );

            await ReserveCollectionItemIdsAsync(
                    dialect,
                    GetUnresolvedCollectionItemIds(rowsToInsert, batchStart, batchCount),
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            await ExecuteCollectionInsertBatchAsync(
                    batchSqlEmitter,
                    tableState.TableWritePlan,
                    rowsToInsert,
                    batchStart,
                    batchCount,
                    rootDocumentId,
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task DeleteOmittedCollectionRowsAsync(
        SqlDialect dialect,
        RelationalWriteMergedTableState tableState,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var mergePlan =
            tableState.TableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableState.TableWritePlan)}' does not have a compiled collection merge plan."
            );
        var retainedStableRowIdentities = GetRetainedStableRowIdentities(tableState);
        List<RelationalWriteMergedTableRow> rowsToDelete = new(tableState.CurrentRows.Length);

        foreach (var currentRow in tableState.CurrentRows)
        {
            var stableRowIdentity = ResolveStableRowIdentityLiteral(
                tableState.TableWritePlan,
                currentRow.Values[mergePlan.StableRowIdentityBindingIndex]
            );

            if (retainedStableRowIdentities.Contains(stableRowIdentity))
            {
                continue;
            }

            rowsToDelete.Add(currentRow);
        }

        await ExecuteParameterizedBatchesAsync(
                dialect,
                tableState.TableWritePlan,
                mergePlan.DeleteByStableRowIdentitySql,
                (batchSqlEmitter, rowCount) =>
                    batchSqlEmitter.EmitCollectionDeleteByStableRowIdentityBatch(
                        tableState.TableWritePlan,
                        rowCount
                    ),
                rowsToDelete,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task UpsertCollectionRowsAsync(
        SqlDialect dialect,
        RelationalWriteMergedTableState tableState,
        long rootDocumentId,
        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var mergePlan =
            tableState.TableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableState.TableWritePlan)}' does not have a compiled collection merge plan."
            );
        Dictionary<long, RelationalWriteMergedTableRow> currentRowsByStableRowIdentity = new(
            tableState.CurrentRows.Length
        );

        foreach (var currentRow in tableState.CurrentRows)
        {
            currentRowsByStableRowIdentity.Add(
                ResolveStableRowIdentityLiteral(
                    tableState.TableWritePlan,
                    currentRow.Values[mergePlan.StableRowIdentityBindingIndex]
                ),
                currentRow
            );
        }

        List<RelationalWriteMergedTableRow> rowsToUpdate = new(tableState.MergedRows.Length);
        List<RelationalWriteMergedTableRow> rowsToInsert = new(tableState.MergedRows.Length);
        var hasOrdinalReorder = false;

        foreach (var mergedRow in tableState.MergedRows)
        {
            var stableRowIdentityValue = mergedRow.Values[mergePlan.StableRowIdentityBindingIndex];

            if (stableRowIdentityValue is FlattenedWriteValue.UnresolvedCollectionItemId)
            {
                rowsToInsert.Add(mergedRow);
                continue;
            }

            var stableRowIdentity = ResolveStableRowIdentityLiteral(
                tableState.TableWritePlan,
                stableRowIdentityValue
            );

            if (!currentRowsByStableRowIdentity.TryGetValue(stableRowIdentity, out var currentRow))
            {
                throw new InvalidOperationException(
                    $"Collection table '{FormatTable(tableState.TableWritePlan)}' produced a merged row for stable identity "
                        + $"'{stableRowIdentity}', but no current row with that identity was loaded."
                );
            }

            if (currentRow.Values.SequenceEqual(mergedRow.Values))
            {
                continue;
            }

            rowsToUpdate.Add(mergedRow);

            if (
                !Equals(
                    currentRow.Values[mergePlan.OrdinalBindingIndex],
                    mergedRow.Values[mergePlan.OrdinalBindingIndex]
                )
            )
            {
                hasOrdinalReorder = true;
            }
        }

        // Batched collection updates emit sequential UPDATE statements. For multi-row reorders, move the affected
        // siblings to temporary negative ordinals first so swaps do not trip the unique (ParentScope, Ordinal)
        // constraint before the final contiguous ordinals are applied.
        if (rowsToUpdate.Count > 1 && hasOrdinalReorder)
        {
            await ExecuteParameterizedBatchesAsync(
                    dialect,
                    tableState.TableWritePlan,
                    mergePlan.UpdateByStableRowIdentitySql,
                    (batchSqlEmitter, rowCount) =>
                        batchSqlEmitter.EmitCollectionUpdateByStableRowIdentityBatch(
                            tableState.TableWritePlan,
                            rowCount
                        ),
                    CreateTemporaryOrdinalRows(rowsToUpdate, mergePlan.OrdinalBindingIndex),
                    rootDocumentId,
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        await ExecuteParameterizedBatchesAsync(
                dialect,
                tableState.TableWritePlan,
                mergePlan.UpdateByStableRowIdentitySql,
                (batchSqlEmitter, rowCount) =>
                    batchSqlEmitter.EmitCollectionUpdateByStableRowIdentityBatch(
                        tableState.TableWritePlan,
                        rowCount
                    ),
                rowsToUpdate,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (rowsToInsert.Count == 0)
        {
            return;
        }

        var batchSqlEmitter = new WritePlanBatchSqlEmitter(dialect);

        for (
            var batchStart = 0;
            batchStart < rowsToInsert.Count;
            batchStart += tableState.TableWritePlan.BulkInsertBatching.MaxRowsPerBatch
        )
        {
            var batchCount = Math.Min(
                tableState.TableWritePlan.BulkInsertBatching.MaxRowsPerBatch,
                rowsToInsert.Count - batchStart
            );

            await ReserveCollectionItemIdsAsync(
                    dialect,
                    GetStableRowIdentityTokens(
                        rowsToInsert,
                        batchStart,
                        batchCount,
                        mergePlan.StableRowIdentityBindingIndex
                    ),
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            await ExecuteCollectionInsertBatchAsync(
                    batchSqlEmitter,
                    tableState.TableWritePlan,
                    rowsToInsert,
                    batchStart,
                    batchCount,
                    rootDocumentId,
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static HashSet<long> GetRetainedStableRowIdentities(RelationalWriteMergedTableState tableState)
    {
        var mergePlan =
            tableState.TableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableState.TableWritePlan)}' does not have a compiled collection merge plan."
            );
        HashSet<long> retainedStableRowIdentities = new(tableState.MergedRows.Length);

        foreach (var mergedRow in tableState.MergedRows)
        {
            var stableRowIdentityValue = mergedRow.Values[mergePlan.StableRowIdentityBindingIndex];

            if (stableRowIdentityValue is FlattenedWriteValue.UnresolvedCollectionItemId)
            {
                continue;
            }

            retainedStableRowIdentities.Add(
                ResolveStableRowIdentityLiteral(tableState.TableWritePlan, stableRowIdentityValue)
            );
        }

        return retainedStableRowIdentities;
    }

    private static long ResolveStableRowIdentityLiteral(
        TableWritePlan tableWritePlan,
        FlattenedWriteValue stableRowIdentityValue
    )
    {
        return stableRowIdentityValue switch
        {
            FlattenedWriteValue.Literal(var value) => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableWritePlan)}' expected a literal stable row identity during persistence."
            ),
        };
    }

    private static async Task ReserveCollectionItemIdAsync(
        SqlDialect dialect,
        FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId,
        IDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (reservedCollectionItemIds.ContainsKey(unresolvedCollectionItemId))
        {
            return;
        }

        var command = BuildReserveCollectionItemIdCommand(dialect);

        await using var dbCommand = writeSession.CreateCommand(command);
        var scalarResult = await dbCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (scalarResult is null or DBNull)
        {
            throw new InvalidOperationException(
                "CollectionItemId reservation did not return a value from dms.CollectionItemIdSequence."
            );
        }

        reservedCollectionItemIds.Add(
            unresolvedCollectionItemId,
            Convert.ToInt64(scalarResult, CultureInfo.InvariantCulture)
        );
    }

    private static async Task ReserveCollectionItemIdsAsync(
        SqlDialect dialect,
        IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> unresolvedCollectionItemIds,
        IDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(unresolvedCollectionItemIds);

        if (unresolvedCollectionItemIds.Count == 0)
        {
            return;
        }

        List<FlattenedWriteValue.UnresolvedCollectionItemId> missingCollectionItemIds = new(
            unresolvedCollectionItemIds.Count
        );

        foreach (var unresolvedCollectionItemId in unresolvedCollectionItemIds)
        {
            if (!reservedCollectionItemIds.ContainsKey(unresolvedCollectionItemId))
            {
                missingCollectionItemIds.Add(unresolvedCollectionItemId);
            }
        }

        if (missingCollectionItemIds.Count == 0)
        {
            return;
        }

        if (missingCollectionItemIds.Count == 1)
        {
            await ReserveCollectionItemIdAsync(
                    dialect,
                    missingCollectionItemIds[0],
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        await using var dbCommand = writeSession.CreateCommand(
            BuildReserveCollectionItemIdsCommand(dialect, missingCollectionItemIds.Count)
        );
        await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var reservedValuesInOrder = await ReadReservedCollectionItemIdsAsync(
                reader,
                missingCollectionItemIds.Count,
                cancellationToken
            )
            .ConfigureAwait(false);

        for (var index = 0; index < missingCollectionItemIds.Count; index++)
        {
            reservedCollectionItemIds.Add(missingCollectionItemIds[index], reservedValuesInOrder[index]);
        }
    }

    private static RelationalCommand BuildReserveCollectionItemIdCommand(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT nextval('"dms"."CollectionItemIdSequence"');
                """,
                []
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                SELECT NEXT VALUE FOR [dms].[CollectionItemIdSequence];
                """,
                []
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }

    private static RelationalCommand BuildReserveCollectionItemIdsCommand(SqlDialect dialect, int count)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                "Reservation count must be at least 1."
            );
        }

        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT
                    series."Ordinal" AS "Ordinal",
                    nextval('"dms"."CollectionItemIdSequence"') AS "CollectionItemId"
                FROM generate_series(1, @count) AS series("Ordinal");
                """,
                [new RelationalParameter("@count", count)]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                WITH [sequence_request] ([Ordinal]) AS (
                    SELECT 1
                    UNION ALL
                    SELECT [Ordinal] + 1
                    FROM [sequence_request]
                    WHERE [Ordinal] < @count
                )
                SELECT
                    [sequence_request].[Ordinal] AS [Ordinal],
                    NEXT VALUE FOR [dms].[CollectionItemIdSequence] OVER (ORDER BY [sequence_request].[Ordinal]) AS [CollectionItemId]
                FROM [sequence_request]
                OPTION (MAXRECURSION 0);
                """,
                [new RelationalParameter("@count", count)]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }

    private static async Task<long[]> ReadReservedCollectionItemIdsAsync(
        DbDataReader reader,
        int expectedCount,
        CancellationToken cancellationToken
    )
    {
        var ordinalColumnOrdinal = reader.GetOrdinal("Ordinal");
        var collectionItemIdColumnOrdinal = reader.GetOrdinal("CollectionItemId");
        var reservedCollectionItemIds = new long[expectedCount];
        var assignedOrdinals = new bool[expectedCount];
        var rowCount = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ordinal = await reader
                .GetFieldValueAsync<int>(ordinalColumnOrdinal, cancellationToken)
                .ConfigureAwait(false);

            if (ordinal < 1 || ordinal > expectedCount)
            {
                throw new InvalidOperationException(
                    $"CollectionItemId reservation returned an out-of-range ordinal value ({ordinal}) for batch size {expectedCount}."
                );
            }

            var index = ordinal - 1;

            if (assignedOrdinals[index])
            {
                throw new InvalidOperationException(
                    $"CollectionItemId reservation returned duplicate ordinal value {ordinal}."
                );
            }

            reservedCollectionItemIds[index] = await reader
                .GetFieldValueAsync<long>(collectionItemIdColumnOrdinal, cancellationToken)
                .ConfigureAwait(false);
            assignedOrdinals[index] = true;
            rowCount++;
        }

        if (rowCount != expectedCount || Array.Exists(assignedOrdinals, static assigned => !assigned))
        {
            throw new InvalidOperationException(
                $"CollectionItemId reservation returned {rowCount} rows for requested batch size {expectedCount}."
            );
        }

        return reservedCollectionItemIds;
    }

    private static async Task ExecuteNonQueryAsync(
        IRelationalWriteSession writeSession,
        RelationalCommand command,
        CancellationToken cancellationToken
    )
    {
        await using var dbCommand = writeSession.CreateCommand(command);
        await dbCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteParameterizedBatchAsync(
        WritePlanBatchSqlEmitter batchSqlEmitter,
        TableWritePlan tableWritePlan,
        string sql,
        Func<WritePlanBatchSqlEmitter, int, string> emitBatchSql,
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        int rowOffset,
        int rowCount,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (rowCount == 0)
        {
            return;
        }

        if (rowCount == 1)
        {
            await ExecuteNonQueryAsync(
                    writeSession,
                    BuildRowCommand(
                        tableWritePlan,
                        sql,
                        rows[rowOffset],
                        rootDocumentId,
                        reservedCollectionItemIds
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        await ExecuteNonQueryAsync(
                writeSession,
                BuildBatchCommand(
                    emitBatchSql(batchSqlEmitter, rowCount),
                    tableWritePlan,
                    rows,
                    rowOffset,
                    rowCount,
                    rootDocumentId,
                    reservedCollectionItemIds
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task ExecuteParameterizedBatchesAsync(
        SqlDialect dialect,
        TableWritePlan tableWritePlan,
        string sql,
        Func<WritePlanBatchSqlEmitter, int, string> emitBatchSql,
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(dialect);

        for (
            var batchStart = 0;
            batchStart < rows.Count;
            batchStart += tableWritePlan.BulkInsertBatching.MaxRowsPerBatch
        )
        {
            var batchCount = Math.Min(
                tableWritePlan.BulkInsertBatching.MaxRowsPerBatch,
                rows.Count - batchStart
            );

            await ExecuteParameterizedBatchAsync(
                    batchSqlEmitter,
                    tableWritePlan,
                    sql,
                    emitBatchSql,
                    rows,
                    batchStart,
                    batchCount,
                    rootDocumentId,
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task ExecuteCollectionInsertBatchAsync(
        WritePlanBatchSqlEmitter batchSqlEmitter,
        TableWritePlan tableWritePlan,
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        int rowOffset,
        int rowCount,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (rowCount == 0)
        {
            return;
        }

        if (rowCount == 1)
        {
            await ExecuteNonQueryAsync(
                    writeSession,
                    BuildRowCommand(
                        tableWritePlan,
                        tableWritePlan.InsertSql,
                        rows[rowOffset],
                        rootDocumentId,
                        reservedCollectionItemIds
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        await ExecuteNonQueryAsync(
                writeSession,
                BuildBatchCommand(
                    batchSqlEmitter.EmitInsertBatch(tableWritePlan, rowCount),
                    tableWritePlan,
                    rows,
                    rowOffset,
                    rowCount,
                    rootDocumentId,
                    reservedCollectionItemIds
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static RelationalWriteMergedTableRow? GetSingleRowOrThrow(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        string rowKind,
        TableWritePlan tableWritePlan
    )
    {
        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidOperationException(
                $"Table '{FormatTable(tableWritePlan)}' produced {rows.Count} {rowKind} rows during no-profile persistence. "
                    + "Only zero or one row is supported before collection merge execution lands."
            ),
        };
    }

    private static IReadOnlyDictionary<
        string,
        RelationalWriteMergedTableRow
    > GetRowsByPhysicalIdentityOrThrow(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        string rowKind,
        TableWritePlan tableWritePlan
    )
    {
        Dictionary<string, RelationalWriteMergedTableRow> rowsByPhysicalIdentity = new(
            rows.Count,
            StringComparer.Ordinal
        );

        foreach (var row in rows)
        {
            var physicalIdentity = ResolvePhysicalRowIdentityKey(tableWritePlan, row);

            if (!rowsByPhysicalIdentity.TryAdd(physicalIdentity, row))
            {
                throw new InvalidOperationException(
                    $"Table '{FormatTable(tableWritePlan)}' produced duplicate {rowKind} rows for aligned scope physical identity '{physicalIdentity}'."
                );
            }
        }

        return rowsByPhysicalIdentity;
    }

    private static string ResolvePhysicalRowIdentityKey(
        TableWritePlan tableWritePlan,
        RelationalWriteMergedTableRow row
    )
    {
        var identityColumns = tableWritePlan.TableModel.IdentityMetadata.PhysicalRowIdentityColumns;

        if (identityColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableWritePlan)}' does not define physical row identity metadata."
            );
        }

        StringBuilder builder = new(CalculatePhysicalRowIdentityKeyCapacity(identityColumns));

        for (var index = 0; index < identityColumns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('|');
            }

            var bindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
                tableWritePlan,
                identityColumns[index]
            );
            builder.Append(identityColumns[index].Value);
            builder.Append('=');
            builder.Append(FormatPhysicalRowIdentityValue(row.Values[bindingIndex]));
        }

        return builder.ToString();
    }

    private static string FormatPhysicalRowIdentityValue(FlattenedWriteValue value)
    {
        return value switch
        {
            FlattenedWriteValue.Literal(var literalValue) => literalValue is null
                ? "literal:<null>"
                : $"literal:{literalValue.GetType().FullName}:{literalValue}",
            FlattenedWriteValue.UnresolvedRootDocumentId => "document:<unresolved>",
            FlattenedWriteValue.UnresolvedCollectionItemId(var token) => $"collection:{token}",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };
    }

    private static int CalculatePhysicalRowIdentityKeyCapacity(IReadOnlyList<DbColumnName> identityColumns)
    {
        var capacity = Math.Max(0, identityColumns.Count - 1);

        foreach (var identityColumn in identityColumns)
        {
            capacity += identityColumn.Value.Length + 1 + 32;
        }

        return capacity;
    }

    private static RelationalCommand BuildRowCommand(
        TableWritePlan tableWritePlan,
        string sql,
        RelationalWriteMergedTableRow row,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    )
    {
        List<RelationalParameter> parameters = new(tableWritePlan.ColumnBindings.Length);

        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            var parameterName = NormalizeParameterName(
                tableWritePlan.ColumnBindings[bindingIndex].ParameterName
            );
            var parameterValue = ResolveParameterValue(
                tableWritePlan,
                row.Values[bindingIndex],
                rootDocumentId,
                reservedCollectionItemIds
            );

            parameters.Add(new RelationalParameter(parameterName, parameterValue));
        }

        return new RelationalCommand(sql, parameters);
    }

    private static RelationalCommand BuildBatchCommand(
        string sql,
        TableWritePlan tableWritePlan,
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        int rowOffset,
        int rowCount,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    )
    {
        List<RelationalParameter> parameters = new(rowCount * tableWritePlan.ColumnBindings.Length);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows[rowOffset + rowIndex];

            for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
            {
                var parameterName = NormalizeParameterName(
                    WriteBatchSqlSupport.BuildBatchParameterName(
                        tableWritePlan.ColumnBindings[bindingIndex].ParameterName,
                        rowIndex
                    )
                );
                var parameterValue = ResolveParameterValue(
                    tableWritePlan,
                    row.Values[bindingIndex],
                    rootDocumentId,
                    reservedCollectionItemIds
                );

                parameters.Add(new RelationalParameter(parameterName, parameterValue));
            }
        }

        return new RelationalCommand(sql, parameters);
    }

    private static IReadOnlyList<RelationalWriteMergedTableRow> CreateTemporaryOrdinalRows(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        int ordinalBindingIndex
    )
    {
        RelationalWriteMergedTableRow[] temporaryRows = new RelationalWriteMergedTableRow[rows.Count];

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var temporaryValues = rows[rowIndex].Values.ToArray();
            temporaryValues[ordinalBindingIndex] = new FlattenedWriteValue.Literal(-1 - rowIndex);

            temporaryRows[rowIndex] = new RelationalWriteMergedTableRow(
                temporaryValues,
                rows[rowIndex].ComparableValues
            );
        }

        return temporaryRows;
    }

    private static IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> GetStableRowIdentityTokens(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        int rowOffset,
        int rowCount,
        int stableRowIdentityBindingIndex
    )
    {
        List<FlattenedWriteValue.UnresolvedCollectionItemId> unresolvedCollectionItemIds = new(rowCount);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (
                rows[rowOffset + rowIndex].Values[stableRowIdentityBindingIndex]
                is FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId
            )
            {
                unresolvedCollectionItemIds.Add(unresolvedCollectionItemId);
            }
        }

        return unresolvedCollectionItemIds;
    }

    private static IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> GetUnresolvedCollectionItemIds(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        int rowOffset,
        int rowCount
    )
    {
        List<FlattenedWriteValue.UnresolvedCollectionItemId> unresolvedCollectionItemIds = new(rowCount);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            AddUnresolvedCollectionItemIds(unresolvedCollectionItemIds, rows[rowOffset + rowIndex].Values);
        }

        return unresolvedCollectionItemIds;
    }

    private static void AddUnresolvedCollectionItemIds(
        List<FlattenedWriteValue.UnresolvedCollectionItemId> unresolvedCollectionItemIds,
        IReadOnlyList<FlattenedWriteValue> values
    )
    {
        foreach (var value in values)
        {
            if (value is FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId)
            {
                unresolvedCollectionItemIds.Add(unresolvedCollectionItemId);
            }
        }
    }

    private static object? ResolveParameterValue(
        TableWritePlan tableWritePlan,
        FlattenedWriteValue value,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    )
    {
        return value switch
        {
            FlattenedWriteValue.Literal(var literalValue) => literalValue,
            FlattenedWriteValue.UnresolvedRootDocumentId => rootDocumentId,
            FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId
                when reservedCollectionItemIds.TryGetValue(
                    unresolvedCollectionItemId,
                    out var reservedCollectionItemId
                ) => reservedCollectionItemId,
            FlattenedWriteValue.UnresolvedCollectionItemId => throw new InvalidOperationException(
                $"Table '{FormatTable(tableWritePlan)}' still contains an unresolved CollectionItemId. "
                    + "CollectionItemId reservation must complete before this row can be written."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };
    }

    private static string NormalizeParameterName(string parameterName)
    {
        return parameterName.StartsWith('@') ? parameterName : $"@{parameterName}";
    }

    private static string FormatTable(TableWritePlan tableWritePlan) =>
        $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";
}

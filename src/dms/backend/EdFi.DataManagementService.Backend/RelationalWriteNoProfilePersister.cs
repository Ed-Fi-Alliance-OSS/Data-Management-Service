// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
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

/// <summary>
/// Applies a merged write one statement per command, in the order
/// <see cref="RelationalWriteDmlStatementPlanner"/> resolved.
/// </summary>
/// <remarks>
/// The co-batched second command carries the same statements in the same order; only the transport differs.
/// Both consume one plan so a change to which statements a write owes, or to the order they are owed in,
/// cannot apply to one path and not the other. This path splits a statement's rows at the table's compiled
/// bulk-insert row cap; the co-batched path packs them against the command's parameter budget instead.
/// </remarks>
internal sealed class RelationalWriteNoProfilePersister(
    IRelationalParameterConfigurator? parameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null,
    ILogger<RelationalWriteNoProfilePersister>? logger = null
) : IRelationalWritePersister
{
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
        var dialect = request.MappingSet.Key.Dialect;

        var rootDocumentId = await ResolveRootDocumentIdAsync(
                request.MappingSet,
                request.WritePlan,
                targetContext,
                mergeResult,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        var collectionItemIdBindings = RelationalWriteCollectionItemIdBindings.Create(dialect, mergeResult);
        var plan = RelationalWriteDmlStatementPlanner.Plan(dialect, mergeResult, collectionItemIdBindings);

        await RelationalWriteCollectionItemIdReservation
            .ReserveAsync(
                dialect,
                plan.CollectionItemIdsToReserve,
                collectionItemIdBindings,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        RelationalWriteRootDocumentIdSource rootDocumentIdSource =
            new RelationalWriteRootDocumentIdSource.Bound(rootDocumentId);

        foreach (var statement in plan.StatementsInOrder)
        {
            await ExecuteStatementAsync(
                    statement,
                    rootDocumentIdSource,
                    collectionItemIdBindings,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var persistObservation = await ReadCommittedPersistObservationAsync(
                dialect,
                rootDocumentId,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new RelationalWritePersistResult(
            rootDocumentId,
            GetTargetDocumentUuid(targetContext),
            persistObservation.ContentVersion,
            persistObservation.DocumentCacheEnqueueOutcome
        );
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
            await ProposedRelationshipAuthorizationCommand
                .ExecuteStandaloneAsync(
                    writeSession,
                    ProposedRelationshipAuthorizationCommand.Build(
                        request.MappingSet,
                        request.WritePlan,
                        relationshipAuthorizationRuntimeCheck,
                        _parameterConfigurator
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            ProposedRelationshipAuthorizationCommand.ThrowMappedFailure(
                request.MappingSet.Key.Dialect,
                _relationshipAuthorizationProviderFailureExtractor,
                _logger,
                relationshipAuthorizationRuntimeCheck,
                ex
            );
            throw;
        }
    }

    private static async Task ExecuteStatementAsync(
        RelationalWriteDmlStatement statement,
        RelationalWriteRootDocumentIdSource rootDocumentId,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var maxRowsPerBatch = statement.TableWritePlan.BulkInsertBatching.MaxRowsPerBatch;

        for (var batchStart = 0; batchStart < statement.Rows.Count; batchStart += maxRowsPerBatch)
        {
            var batchCount = Math.Min(maxRowsPerBatch, statement.Rows.Count - batchStart);
            var command =
                batchCount == 1
                    ? RelationalWriteRowStatements.BuildRowCommand(
                        statement.TableWritePlan,
                        statement.SingleRowSql,
                        statement.Rows[batchStart],
                        rootDocumentId,
                        collectionItemIdBindings
                    )
                    : RelationalWriteRowStatements.BuildBatchCommand(
                        statement.EmitBatchSql(batchCount),
                        statement.TableWritePlan,
                        statement.Rows,
                        batchStart,
                        batchCount,
                        rootDocumentId,
                        collectionItemIdBindings
                    );

            await using var dbCommand = writeSession.CreateCommand(command);
            await dbCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static DocumentUuid GetTargetDocumentUuid(RelationalWriteTargetContext targetContext) =>
        targetContext switch
        {
            RelationalWriteTargetContext.CreateNew(var documentUuid) => documentUuid,
            RelationalWriteTargetContext.ExistingDocument(_, var documentUuid, _) => documentUuid,
            _ => throw new ArgumentOutOfRangeException(nameof(targetContext), targetContext, null),
        };

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
        var command = RelationalDocumentRowCommandBuilder.BuildInsertCommand(
            mappingSet.Key.Dialect,
            documentUuid,
            RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource)
        );
        var relationshipAuthorizationRuntimeCheck = mergeResult.ProposedRelationshipAuthorizationRuntimeCheck;

        try
        {
            if (relationshipAuthorizationRuntimeCheck is not null)
            {
                return await ExecuteAuthorizedInsertDocumentAsync(
                        writeSession,
                        BuildAuthorizedInsertDocumentCommand(
                            mappingSet,
                            writePlan,
                            relationshipAuthorizationRuntimeCheck,
                            command
                        ),
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
            ProposedRelationshipAuthorizationCommand.ThrowMappedFailure(
                mappingSet.Key.Dialect,
                _relationshipAuthorizationProviderFailureExtractor,
                _logger,
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

    private static async Task<RelationalWritePersistObservation> ReadCommittedPersistObservationAsync(
        SqlDialect dialect,
        long rootDocumentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        await using var command = writeSession.CreateCommand(
            RelationalDocumentLockCommandBuilder.BuildContentVersionWithDocumentCacheEnqueueOutcomeCommand(
                dialect,
                rootDocumentId
            )
        );
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Relational write persistence found no ContentVersion for committed document id {rootDocumentId}."
            );
        }

        var observation = new RelationalWritePersistObservation(
            Convert.ToInt64(
                reader.GetValue(reader.GetOrdinal("ContentVersion")),
                CultureInfo.InvariantCulture
            ),
            RequireEnqueueOutcome(
                Convert.ToInt32(
                    reader.GetValue(reader.GetOrdinal("DocumentCacheEnqueueOutcome")),
                    CultureInfo.InvariantCulture
                )
            )
        );

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Relational write persistence returned more than one persisted-target observation."
            );
        }

        return observation;
    }

    private static DocumentCacheEnqueueOutcome RequireEnqueueOutcome(int value)
    {
        var outcome = (DocumentCacheEnqueueOutcome)value;
        if (!Enum.IsDefined(outcome))
        {
            throw new InvalidOperationException(
                $"Relational write persistence returned unsupported DocumentCache enqueue outcome '{value}'."
            );
        }

        return outcome;
    }

    private sealed record RelationalWritePersistObservation(
        long ContentVersion,
        DocumentCacheEnqueueOutcome DocumentCacheEnqueueOutcome
    );

    private static async Task<long> ExecuteAuthorizedInsertDocumentAsync(
        IRelationalWriteSession writeSession,
        RelationalCommand command,
        QualifiedResourceName resource,
        CancellationToken cancellationToken
    )
    {
        await using var dbCommand = writeSession.CreateCommand(command);
        await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await ProposedRelationshipAuthorizationCommand
            .ReadAndValidateResultAsync(reader, cancellationToken)
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

    private RelationalCommand BuildAuthorizedInsertDocumentCommand(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        ProposedRelationshipAuthorizationRuntimeCheck relationshipAuthorizationRuntimeCheck,
        RelationalCommand insertDocumentCommand
    )
    {
        var proposedAuthorizationCommand = ProposedRelationshipAuthorizationCommand.Build(
            mappingSet,
            writePlan,
            relationshipAuthorizationRuntimeCheck,
            _parameterConfigurator
        );

        return new RelationalCommand(
            $"{proposedAuthorizationCommand.CommandText}{Environment.NewLine}{insertDocumentCommand.CommandText}",
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
}

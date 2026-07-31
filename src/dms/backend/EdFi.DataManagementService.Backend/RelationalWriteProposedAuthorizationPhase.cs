// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// What the proposed-authorization phase decided: the merge result it may have enriched with the
/// extracted runtime check, and an immediate result when authorization denied or its plan could not be
/// reconciled.
/// </summary>
internal sealed record RelationalWriteProposedAuthorizationResolution(
    RelationalWriteMergeResult MergeResult,
    RelationalWriteExecutorResult? ImmediateResult
);

/// <summary>
/// Evaluates proposed-value authorization for a request that will issue no data-modifying statement.
/// </summary>
/// <remarks>
/// A seam, so executor orchestration tests can substitute the sequential shape and keep asserting
/// precedence through a boundary, while production SQL construction, statement order, the ordered-segment
/// fallback, and failure mapping are owned by the composite implementation's own fixture.
/// </remarks>
internal interface IRelationalWriteProposedAuthorizationPhase
{
    Task<RelationalWriteProposedAuthorizationResolution> ResolveAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// The write path's second command in authorization-only mode: the proposed namespace <c>AUTH1</c>
/// statement followed by the proposed relationship <c>AUTH1</c> statement, co-batched into one command
/// and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This phase serves the situations that require no data-modifying statement — a guarded no-op, a
/// deferred etag precondition failure, a deferred missing-document-reference failure, and any
/// coincidence of them. Because all of those are decided in process from the merge result and the
/// hydrated current state, the executor knows before it sends anything that no DML will follow, and one
/// command can carry every proposed check the DML path would have carried. A request that is several of
/// those situations at once still costs one command rather than one per condition.
/// </para>
/// <para>
/// Statement order is precedence order. The command aborts at its first <c>AUTH1</c>, so emitting
/// namespace before relationship is what makes a namespace denial win over a concurrent relationship
/// denial — the same ordering the stored-value checks use in the first phase. The deferred dispositions
/// that need no statement of their own (a caller with no claims, a proposed plan that cannot be
/// reconciled with the finalized root row) are held back until the command has run, so a namespace
/// denial still outranks them.
/// </para>
/// <para>
/// Proposed authorization cannot be hoisted into the first phase: its statements bind values taken from
/// the finalized merged root row, which does not exist until the first command's hydration result sets
/// are decoded and the merge runs. The two-command floor for an authorized write is structural.
/// </para>
/// </remarks>
internal sealed class CompositeRelationalWriteProposedAuthorization(
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null,
    ILogger? logger = null,
    RelationalCommandBudget? commandBudget = null
) : IRelationalWriteProposedAuthorizationPhase
{
    private const string ProposedNamespaceAuthorizationLabel = "proposed-namespace-authorization";
    private const string ProposedRelationshipAuthorizationLabel = "proposed-relationship-authorization";

    private readonly IRelationalParameterConfigurator _relationalParameterConfigurator =
        relationalParameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;

    private readonly IRelationshipAuthorizationProviderFailureExtractor _providerFailureExtractor =
        relationshipAuthorizationProviderFailureExtractor
        ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    private readonly RelationalCommandBudget? _commandBudget = commandBudget;

    public async Task<RelationalWriteProposedAuthorizationResolution> ResolveAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mergeResult);
        ArgumentNullException.ThrowIfNull(writeSession);

        if (TryPlanNamespace(request, mergeResult, out var namespacePlan) is { } namespacePlanFailure)
        {
            // The plan could not be reconciled with the finalized root row, which is decided before any
            // statement is built, so nothing is sent.
            return new RelationalWriteProposedAuthorizationResolution(mergeResult, namespacePlanFailure);
        }

        var relationshipPlan = PlanRelationship(request, mergeResult);
        mergeResult = relationshipPlan.MergeResult;

        if (namespacePlan is null && relationshipPlan.RuntimeCheck is null)
        {
            // Nothing to authorize. A deferred disposition may still exist — a caller with no claims —
            // and it needs no command of its own.
            return new RelationalWriteProposedAuthorizationResolution(
                mergeResult,
                relationshipPlan.DeferredResult
            );
        }

        var deniedResult = await ExecuteAsync(
                request,
                namespacePlan,
                relationshipPlan.RuntimeCheck,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new RelationalWriteProposedAuthorizationResolution(
            mergeResult,
            deniedResult ?? relationshipPlan.DeferredResult
        );
    }

    private async Task<RelationalWriteExecutorResult?> ExecuteAsync(
        RelationalWriteExecutorRequest request,
        NamespaceStatementPlan? namespacePlan,
        ProposedRelationshipAuthorizationRuntimeCheck? runtimeCheck,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(request.MappingSet.Key.Dialect),
            _commandBudget
        );

        var namespaceEmitted = TryAppendNamespace(builder, request, namespacePlan);
        var relationshipCommand = runtimeCheck is null
            ? null
            : ProposedRelationshipAuthorizationCommand.Build(
                request.MappingSet,
                request.WritePlan,
                runtimeCheck,
                _relationalParameterConfigurator
            );
        var relationshipEmitted =
            relationshipCommand is not null
            && CanCoBatchRelationship(runtimeCheck!, relationshipCommand, builder)
            && TryAppendRelationship(builder, relationshipCommand);

        if (namespaceEmitted || relationshipEmitted)
        {
            try
            {
                await new RelationalCompositeCommandExecution()
                    .ExecuteAsync(writeSession, builder.Seal(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DbException exception)
            {
                return MapAuthorizationFailure(
                    request,
                    namespaceEmitted ? namespacePlan : null,
                    relationshipEmitted ? runtimeCheck : null,
                    exception
                );
            }
        }

        if (relationshipCommand is null || relationshipEmitted)
        {
            return null;
        }

        // A structured claim parameterization or a combined parameter budget that does not fit selects
        // an ordered segment on the same session and transaction rather than a co-batched statement.
        return await ExecuteStandaloneRelationshipAsync(
                request,
                runtimeCheck!,
                relationshipCommand,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<RelationalWriteExecutorResult?> ExecuteStandaloneRelationshipAsync(
        RelationalWriteExecutorRequest request,
        ProposedRelationshipAuthorizationRuntimeCheck runtimeCheck,
        RelationalCommand relationshipCommand,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await ProposedRelationshipAuthorizationCommand
                .ExecuteStandaloneAsync(writeSession, relationshipCommand, cancellationToken)
                .ConfigureAwait(false);

            return null;
        }
        catch (DbException exception)
        {
            return MapAuthorizationFailure(request, namespacePlan: null, runtimeCheck, exception);
        }
    }

    /// <summary>
    /// A structured claim list binds as a table-valued parameter, which the composite rewriter cannot
    /// rename into a co-batched statement, and a statement that does not fit the command's remaining
    /// parameter budget must not overflow it. Either condition selects the ordered-segment path.
    /// </summary>
    private static bool CanCoBatchRelationship(
        ProposedRelationshipAuthorizationRuntimeCheck runtimeCheck,
        RelationalCommand relationshipCommand,
        RelationalCompositeCommandBuilder builder
    ) =>
        runtimeCheck.ClaimEducationOrganizationIdParameterization.Kind
            is not AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured
        && builder.Fits(relationshipCommand.Parameters.Count);

    private sealed record NamespaceStatementPlan(
        IReadOnlyList<NamespaceAuthorizationCheckSpec> Checks,
        NamespacePrefixParameterization PrefixParameterization,
        string? ProposedNamespace
    );

    /// <summary>
    /// Extracts the proposed namespace value from the finalized merged root row — never the raw request
    /// body — so the <c>LIKE</c> semantics and the AUTH1 failure mapping stay identical to the read path.
    /// Returns the security-configuration failure when the plan cannot be reconciled with that row.
    /// </summary>
    private static RelationalWriteExecutorResult? TryPlanNamespace(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        out NamespaceStatementPlan? statementPlan
    )
    {
        statementPlan = null;

        if (request.ProposedNamespaceAuthorization is not { } namespaceAuthorization)
        {
            return null;
        }

        var extraction = ProposedNamespaceValueExtractor.Extract(
            namespaceAuthorization.Checks,
            RelationalWriteFinalizedRootRow.Build(request, mergeResult)
        );

        if (extraction is ProposedNamespaceValueExtractionResult.InvalidAuthorizationPlan invalid)
        {
            return RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                request.OperationKind,
                [invalid.FailureMessage],
                AuthorizationSecurityConfigurationDiagnostics.ForNamespaceProposedValueExtraction(
                    namespaceAuthorization.Checks
                )
            );
        }

        var ready = (ProposedNamespaceValueExtractionResult.Ready)extraction;

        statementPlan = new NamespaceStatementPlan(
            namespaceAuthorization.Checks,
            namespaceAuthorization.NamespacePrefixParameterization,
            ready.ProposedNamespace
        );

        return null;
    }

    private static bool TryAppendNamespace(
        RelationalCompositeCommandBuilder builder,
        RelationalWriteExecutorRequest request,
        NamespaceStatementPlan? statementPlan
    )
    {
        if (statementPlan is null)
        {
            return false;
        }

        var sqlPlan = new NamespaceAuthorizationSqlCompiler(request.MappingSet.Key.Dialect).Compile(
            new NamespaceAuthorizationSqlSpec(
                statementPlan.Checks,
                statementPlan.PrefixParameterization,
                NamespaceAuthorizationSqlSpecDefaults.DocumentIdParameterName,
                NamespaceAuthorizationSqlSpecDefaults.ProposedNamespaceParameterName
            )
        );
        var command = NamespaceAuthorizationExecutor.BuildCommand(
            sqlPlan,
            new NamespaceAuthorizationExecutionRequest(
                request.MappingSet,
                // Proposed-only checks evaluate the bound proposed value; no stored DocumentId is bound.
                DocumentId: 0L,
                statementPlan.ProposedNamespace,
                statementPlan.Checks,
                statementPlan.PrefixParameterization
            )
        );
        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            command,
            builder.Allocator,
            builder.NextOrdinal
        );
        var resultSetCount = statementPlan.Checks.Count;

        builder.Append(
            ProposedNamespaceAuthorizationLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            (reader, readCancellation) =>
                RelationalCompositeResultSetSpan.ConsumeAsync(reader, resultSetCount, readCancellation),
            resultSetCount
        );

        return true;
    }

    private static bool TryAppendRelationship(
        RelationalCompositeCommandBuilder builder,
        RelationalCommand relationshipCommand
    )
    {
        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            relationshipCommand,
            builder.Allocator,
            builder.NextOrdinal
        );

        builder.Append(
            ProposedRelationshipAuthorizationLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            ProposedRelationshipAuthorizationCommand.ReadAndValidateResultAsync
        );

        return true;
    }

    private sealed record RelationshipStatementPlan(
        RelationalWriteMergeResult MergeResult,
        ProposedRelationshipAuthorizationRuntimeCheck? RuntimeCheck,
        RelationalWriteExecutorResult? DeferredResult
    );

    private static RelationshipStatementPlan PlanRelationship(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult
    )
    {
        switch (request.ProposedRelationshipAuthorization)
        {
            case null:
            case RelationshipAuthorizationResult.NoAuthorizationRequired:
            case RelationshipAuthorizationResult.NoFurtherAuthorizationRequired:
                return new RelationshipStatementPlan(mergeResult, null, null);

            // NoClaims is deferred from POST or PUT preflight so the proposed namespace check can run
            // first: namespace AND-composes before the relationship OR-group, so its denial must win.
            case RelationshipAuthorizationResult.NoClaims noClaims:
                return new RelationshipStatementPlan(
                    mergeResult,
                    null,
                    RelationalWriteExecutorResults.BuildNoClaimsRelationshipAuthorizationResult(
                        request.OperationKind,
                        noClaims
                    )
                );

            case RelationshipAuthorizationResult.Authorized authorized:
                var extractionResult = RelationshipAuthorizationProposedValueExtractor.Extract(
                    authorized,
                    RelationalWriteFinalizedRootRow.Build(request, mergeResult),
                    RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(
                        request.OperationKind
                    ),
                    request.TargetContext
                );

                return extractionResult switch
                {
                    ProposedRelationshipAuthorizationExtractionResult.Ready ready =>
                        new RelationshipStatementPlan(
                            mergeResult with
                            {
                                ProposedRelationshipAuthorizationRuntimeCheck = ready.RuntimeCheck,
                            },
                            ready.RuntimeCheck,
                            null
                        ),
                    ProposedRelationshipAuthorizationExtractionResult.InvalidAuthorizationPlan invalid =>
                        new RelationshipStatementPlan(
                            mergeResult,
                            null,
                            RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                                request.OperationKind,
                                [invalid.FailureMessage],
                                AuthorizationSecurityConfigurationDiagnostics.ForRelationshipProposedValueExtraction(
                                    authorized.CheckSpecs
                                )
                            )
                        ),
                    _ => throw new InvalidOperationException(
                        $"Unsupported proposed relationship authorization extraction result '{extractionResult.GetType().Name}'."
                    ),
                };

            default:
                throw new InvalidOperationException(
                    $"Unsupported proposed relationship authorization result '{request.ProposedRelationshipAuthorization.GetType().Name}'."
                );
        }
    }

    /// <summary>
    /// Maps a provider failure through the same mappers the standalone executors use, so a denial
    /// carried by the AUTH1 device produces exactly the result it always has. Routing is by the payload's
    /// own discriminator rather than by statement position, so it is correct whichever statement aborted.
    /// A failure that is no authorization denial at all propagates to the executor's database failure
    /// handling unchanged.
    /// </summary>
    private RelationalWriteExecutorResult MapAuthorizationFailure(
        RelationalWriteExecutorRequest request,
        NamespaceStatementPlan? namespacePlan,
        ProposedRelationshipAuthorizationRuntimeCheck? runtimeCheck,
        DbException exception
    )
    {
        var dialect = request.MappingSet.Key.Dialect;

        if (namespacePlan is not null)
        {
            var plannedCheckValueSources = namespacePlan
                .Checks.Select(static check => check.ValueSource)
                .ToArray();

            if (
                NamespaceAuthorizationProviderFailureMapper.TryMapNamespaceAuthorizationFailure(
                    dialect,
                    exception,
                    _providerFailureExtractor,
                    plannedCheckValueSources,
                    namespacePlan.PrefixParameterization.ConfiguredPrefixesInOrder,
                    out var namespaceFailure
                )
            )
            {
                return RelationalWriteExecutorResults.BuildNamespaceAuthorizationFailureResult(
                    request.OperationKind,
                    namespaceFailure!
                );
            }

            if (
                NamespaceAuthorizationProviderFailureMapper.TryBuildInvalidAuthorizationFailureDiagnostics(
                    dialect,
                    exception,
                    _providerFailureExtractor,
                    plannedCheckValueSources,
                    namespacePlan.Checks,
                    out var namespaceDiagnostics
                )
            )
            {
                return RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                    request.OperationKind,
                    [NamespaceAuthorizationSecurityConfigurationMessages.InvalidAuthorizationMetadata],
                    namespaceDiagnostics
                );
            }

            // Proposed-value checks bind no stored DocumentId, so the stale stored-target kind cannot be
            // raised by this statement; it is left to the relationship mapping and then to the
            // executor's unmapped-failure handling.
        }

        if (runtimeCheck is not null)
        {
            // Throws the authorization exceptions the executor already maps to results, or rethrows the
            // provider failure unchanged when it is not an authorization denial.
            ProposedRelationshipAuthorizationCommand.ThrowMappedFailure(
                dialect,
                _providerFailureExtractor,
                _logger,
                runtimeCheck,
                exception
            );
        }

        throw exception;
    }
}

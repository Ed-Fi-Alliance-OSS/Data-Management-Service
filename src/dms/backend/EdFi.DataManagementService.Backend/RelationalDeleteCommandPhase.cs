// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// One relational DELETE's inputs, resolved by the repository's preflight before the write session opens.
/// </summary>
internal sealed record RelationalDeleteCommandRequest(
    MappingSet MappingSet,
    QualifiedResourceName Resource,
    DocumentUuid DocumentUuid,
    TraceId TraceId,
    RelationalWriteNamespaceAuthorization? StoredNamespaceAuthorization,
    RelationshipAuthorizationResult StoredRelationshipAuthorization
)
{
    public WritePrecondition WritePrecondition { get; init; } = new WritePrecondition.None();

    /// <summary>
    /// The denial a caller holding no usable claims has already earned, which needs no statement of its own.
    /// It is held back until the capture proves the target exists, so a missing target still answers
    /// not-found rather than forbidden.
    /// </summary>
    public DeleteResult? DeferredRelationshipDenial { get; init; }
}

/// <summary>
/// The relational DELETE's command stream: target capture and lock, stored authorization, the resource root
/// delete, and the <c>dms.Document</c> delete, co-batched into one command.
/// </summary>
/// <remarks>
/// <para>
/// The root row is deleted before <c>dms.Document</c> so the tombstone trigger can still read the
/// <c>DocumentUuid</c>. Both deletes key on the capture carrier rather than repeating the target predicate,
/// so a concurrent create landing after the capture cannot be deleted by a request that never locked it, and
/// an absent capture leaves both deletes matching nothing.
/// </para>
/// <para>
/// The authorization statements precede the deletes in the same command and abort it through the AUTH1
/// device, so a denial's transaction never reaches a delete.
/// </para>
/// </remarks>
internal sealed class CompositeRelationalDeleteCommand(
    IRelationalDeleteEtagPreconditionChecker etagPreconditionChecker,
    IRelationalWriteExceptionClassifier exceptionClassifier,
    IRelationalDeleteConstraintResolver deleteConstraintResolver,
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null,
    ILogger? logger = null,
    RelationalCommandBudget? commandBudget = null
)
{
    private const string ResourceRootDeleteLabel = "resource-root-delete";
    private const string DocumentDeleteLabel = "document-delete";

    /// <summary>
    /// Stands in for the bound document id the standalone delete builders declare. Every co-batched delete
    /// substitutes the carrier expression for it, so the value is never sent.
    /// </summary>
    private const long DocumentIdSubstitutedAtEmission = 0L;

    /// <summary>
    /// DELETE emits at most one relationship check, so it owns index 0 of the request's AUTH1 space.
    /// </summary>
    public const int RelationshipAuthorizationAuth1Index = 0;

    private readonly IRelationalDeleteEtagPreconditionChecker _etagPreconditionChecker =
        etagPreconditionChecker ?? throw new ArgumentNullException(nameof(etagPreconditionChecker));

    private readonly IRelationalWriteExceptionClassifier _exceptionClassifier =
        exceptionClassifier ?? throw new ArgumentNullException(nameof(exceptionClassifier));

    private readonly IRelationalDeleteConstraintResolver _deleteConstraintResolver =
        deleteConstraintResolver ?? throw new ArgumentNullException(nameof(deleteConstraintResolver));

    private readonly IRelationalParameterConfigurator _relationalParameterConfigurator =
        relationalParameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;

    private readonly IRelationshipAuthorizationProviderFailureExtractor _providerFailureExtractor =
        relationshipAuthorizationProviderFailureExtractor
        ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    private readonly RelationalCommandBudget? _commandBudget = commandBudget;

    /// <param name="NamespacePlan">
    /// The namespace checks the opening command carried, or <see langword="null"/> when it carried none —
    /// so a provider failure is mapped only against checks that command actually sent.
    /// </param>
    /// <param name="NamespaceSegmentAuthorization">
    /// The namespace authorization still owed as its own ordered segment because it did not fit the opening
    /// command, or <see langword="null"/> when nothing is owed.
    /// </param>
    /// <param name="DocumentDeleteOrdinal">
    /// Where the <c>dms.Document</c> delete's outcome lands, or <see langword="null"/> when the deletes were
    /// withheld — a deferred denial had already decided the request, the precondition must be compared
    /// first, or an authorization check still has to run and pass as its own segment.
    /// </param>
    private sealed record DeleteCommandPlan(
        RelationalCompositeCommand Command,
        StoredNamespaceStatementPlan? NamespacePlan,
        RelationalWriteNamespaceAuthorization? NamespaceSegmentAuthorization,
        StoredRelationshipStatementPlan RelationshipPlan,
        int? DocumentDeleteOrdinal
    );

    public async Task<DeleteResult> ExecuteAsync(
        RelationalDeleteCommandRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writeSession);

        var relationshipPlan = RelationalCompositeStoredAuthorization.Classify(
            request.StoredRelationshipAuthorization
        );
        var plan = BuildPlan(request, relationshipPlan);

        IReadOnlyList<RelationalCompositeStatementOutcome> outcomes;

        try
        {
            outcomes = await new RelationalCompositeCommandExecution()
                .ExecuteAsync(writeSession, plan.Command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            return MapProviderFailure(request, plan.NamespacePlan, plan.RelationshipPlan, exception);
        }

        if (outcomes[0].Value is not RelationalCompositeCapturedTarget capturedTarget)
        {
            return BuildMissingTargetResult(request);
        }

        if (
            await ExecuteSegmentedNamespaceAsync(
                    request,
                    plan.NamespaceSegmentAuthorization,
                    capturedTarget.DocumentId,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false) is
            { } namespaceResult
        )
        {
            return namespaceResult;
        }

        if (
            await ResolveStoredRelationshipAsync(
                    request,
                    plan.RelationshipPlan,
                    outcomes,
                    capturedTarget,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false) is
            { } relationshipResult
        )
        {
            return relationshipResult;
        }

        // Existence and concurrency were settled by the capture, and its ContentVersion is the stamp the
        // precondition compares against — no re-lock and no state hydration. A wildcard matches
        // unconditionally because the captured row exists; the missing-target split is handled above.
        if (
            request.WritePrecondition is WritePrecondition.IfMatch ifMatch
            && !_etagPreconditionChecker
                .Evaluate(
                    request.MappingSet,
                    new RelationalWriteTargetContext.ExistingDocument(
                        capturedTarget.DocumentId,
                        new DocumentUuid(capturedTarget.DocumentUuid),
                        capturedTarget.ContentVersion
                    ),
                    ifMatch
                )
                .IsMatch
        )
        {
            return new DeleteResult.DeleteFailureETagMisMatch();
        }

        if (plan.DocumentDeleteOrdinal is { } documentDeleteOrdinal)
        {
            return outcomes[documentDeleteOrdinal].Value is true
                ? new DeleteResult.DeleteSuccess()
                : new DeleteResult.DeleteFailureNotExists();
        }

        return await ExecuteDeleteSegmentAsync(
                request,
                capturedTarget.DocumentId,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the command the request opens with. It carries the deletes too whenever nothing has to be
    /// decided in process between observing the target and modifying it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three conditions withhold them. A specific-tag <c>If-Match</c> must be compared before the method is
    /// applied — the compare is over a served etag's string projection, so it cannot be expressed as a SQL
    /// guard without a second implementation of the etag semantics, and co-batching the deletes anyway would
    /// let an inbound foreign-key violation preempt the precondition failure the caller must see. A
    /// relationship check that cannot be co-batched must likewise pass before any row is deleted. And an
    /// authorization statement that does not fit this command's parameter budget owes an ordered segment of
    /// its own, which must run and pass first — otherwise the deletes would execute with a check that was
    /// silently never sent.
    /// </para>
    /// <para>
    /// A namespace check that does not fit also forces the relationship check onto a segment: emitting the
    /// relationship into this command would place it ahead of the namespace check whose denial outranks it.
    /// </para>
    /// </remarks>
    private DeleteCommandPlan BuildPlan(
        RelationalDeleteCommandRequest request,
        StoredRelationshipStatementPlan classifiedRelationship
    )
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(request.MappingSet.Key.Dialect),
            _commandBudget
        );
        var carrier = builder.Carrier;

        AppendCaptureTarget(builder, request);

        var namespaceEmitted = RelationalCompositeStoredAuthorization.TryAppendNamespace(
            builder,
            carrier,
            request.MappingSet,
            request.StoredNamespaceAuthorization,
            out var namespacePlan
        );
        var relationshipPlan = classifiedRelationship;
        var relationshipEmitted =
            namespaceEmitted
            && RelationalCompositeStoredAuthorization.TryAppendRelationship(
                builder,
                carrier,
                request.MappingSet,
                classifiedRelationship,
                RelationshipAuthorizationAuth1Index,
                _relationalParameterConfigurator,
                out relationshipPlan
            );

        if (!relationshipEmitted && relationshipPlan.Disposition is StoredRelationshipDisposition.Emitted)
        {
            // The ordered segment a table-valued claim list already needs serves a check that simply does
            // not fit as well: it runs against the decoded captured id, under the lock the capture holds.
            relationshipPlan = relationshipPlan with
            {
                Disposition = StoredRelationshipDisposition.Standalone,
            };
        }

        int? documentDeleteOrdinal = null;

        if (namespaceEmitted && CanCoBatchDeletes(request, relationshipPlan.Disposition))
        {
            AppendResourceRootDelete(builder, carrier, request);
            documentDeleteOrdinal = AppendDocumentDelete(builder, carrier, request.MappingSet.Key.Dialect);
        }

        return new DeleteCommandPlan(
            builder.Seal(),
            namespacePlan,
            namespaceEmitted ? null : request.StoredNamespaceAuthorization,
            relationshipPlan,
            documentDeleteOrdinal
        );
    }

    private static bool CanCoBatchDeletes(
        RelationalDeleteCommandRequest request,
        StoredRelationshipDisposition disposition
    ) =>
        disposition is StoredRelationshipDisposition.None or StoredRelationshipDisposition.Emitted
        && request.WritePrecondition is not WritePrecondition.IfMatch { IsWildcard: false };

    /// <summary>
    /// Runs the stored namespace checks as their own ordered segment against the decoded captured id, for
    /// the case where they could not fit the opening command. Authorization therefore still strictly
    /// precedes the relationship check and any deletion, at the cost of one more command on that path.
    /// </summary>
    private async Task<DeleteResult?> ExecuteSegmentedNamespaceAsync(
        RelationalDeleteCommandRequest request,
        RelationalWriteNamespaceAuthorization? namespaceAuthorization,
        long documentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (namespaceAuthorization is null)
        {
            return null;
        }

        return await StoredNamespaceAuthorizationExecution
            .ExecuteAsync<DeleteResult>(
                writeSession.CreateCommandExecutor(),
                _providerFailureExtractor,
                request.MappingSet,
                documentId,
                namespaceAuthorization,
                failure => new DeleteResult.DeleteFailureNamespaceNotAuthorized(failure),
                (failureMessage, diagnostics) =>
                    new DeleteResult.DeleteFailureSecurityConfiguration([failureMessage], diagnostics),
                // Stale is unreachable while the capture lock holds; the same defensive mapping the
                // co-batched classification uses.
                static () => new DeleteResult.DeleteFailureNotExists(),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the deletes as their own command on the same session and transaction, binding the decoded
    /// captured id. The original row lock is still held, so this observes exactly the row the capture did.
    /// </summary>
    private async Task<DeleteResult> ExecuteDeleteSegmentAsync(
        RelationalDeleteCommandRequest request,
        long documentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    ) =>
        await RelationalDeleteExecution
            .TryExecuteAsync(
                writeSession.CreateCommandExecutor(),
                OrderedDeleteCommandBuilder.BuildResourceDeleteByDocumentIdCommand(
                    request.MappingSet.Key.Dialect,
                    RequireRelationalRootTable(request),
                    documentId
                ),
                _exceptionClassifier,
                _deleteConstraintResolver,
                request.MappingSet.Model,
                _logger,
                request.DocumentUuid,
                request.TraceId,
                DeleteTargetKind.Document,
                cancellationToken
            )
            .ConfigureAwait(false);

    /// <summary>
    /// The resource's root table, rejecting a storage kind the regular-resource delete path cannot serve.
    /// </summary>
    private static DbTableName RequireRelationalRootTable(RelationalDeleteCommandRequest request)
    {
        var concreteResource = request.MappingSet.GetConcreteResourceModelOrThrow(request.Resource);

        if (concreteResource.StorageKind is not ResourceStorageKind.RelationalTables)
        {
            throw new InvalidOperationException(
                $"Resource '{RelationalWriteSupport.FormatResource(request.Resource)}' cannot use the "
                    + "regular-resource delete path because its storage kind is "
                    + $"'{concreteResource.StorageKind}'."
            );
        }

        return concreteResource.RelationalModel.Root.Table;
    }

    private static void AppendCaptureTarget(
        RelationalCompositeCommandBuilder builder,
        RelationalDeleteCommandRequest request
    )
    {
        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            RelationalWriteTargetLookupSupport.BuildDocumentUuidCaptureTargetPredicate(
                request.MappingSet,
                request.Resource,
                request.DocumentUuid
            ),
            builder.Allocator,
            builder.NextOrdinal
        );

        builder.AppendCaptureTarget(rewritten.Sql, rewritten.Parameters);
    }

    private static void AppendResourceRootDelete(
        RelationalCompositeCommandBuilder builder,
        IRelationalCompositeTargetCarrier carrier,
        RelationalDeleteCommandRequest request
    )
    {
        var rewritten = RewriteAgainstCarrier(
            builder,
            carrier,
            OrderedDeleteCommandBuilder.BuildResourceRootDeleteByDocumentIdCommand(
                request.MappingSet.Key.Dialect,
                RequireRelationalRootTable(request),
                DocumentIdSubstitutedAtEmission
            )
        );

        builder.Append(
            ResourceRootDeleteLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Sentinel
        );
    }

    private static int AppendDocumentDelete(
        RelationalCompositeCommandBuilder builder,
        IRelationalCompositeTargetCarrier carrier,
        SqlDialect dialect
    )
    {
        var rewritten = RewriteAgainstCarrier(
            builder,
            carrier,
            OrderedDeleteCommandBuilder.BuildDocumentDeleteByDocumentIdCommand(
                dialect,
                DocumentIdSubstitutedAtEmission
            )
        );

        return builder.Append(
            DocumentDeleteLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            ReadDeletedDocumentRowAsync
        );
    }

    private static RelationalCompositeRewrittenStatement RewriteAgainstCarrier(
        RelationalCompositeCommandBuilder builder,
        IRelationalCompositeTargetCarrier carrier,
        RelationalCommand command
    ) =>
        RelationalCompositeStatementRewriter.Rewrite(
            command,
            builder.Allocator,
            builder.NextOrdinal,
            RelationalCompositeStoredAuthorization.BuildCarrierSubstitutions(
                carrier,
                "documentId",
                carrier.CapturedTargetIdExpression
            )
        );

    /// <summary>
    /// RFC 9110 §13.1.1 <c>If-Match: *</c> requires the target to exist, so a wildcard against a missing
    /// DELETE target yields the precondition-failed result rather than not-exists.
    /// </summary>
    private static DeleteResult BuildMissingTargetResult(RelationalDeleteCommandRequest request) =>
        request.WritePrecondition is WritePrecondition.IfMatch { IsWildcard: true }
            ? new DeleteResult.DeleteFailureETagMisMatch(ETagPreconditionFailureReason.TargetDoesNotExist)
            : new DeleteResult.DeleteFailureNotExists();

    /// <summary>
    /// Resolves what the stored relationship authorization decided. A denial already surfaced as a mapped
    /// provider failure; what remains is the decoded success row and the dispositions that need no statement.
    /// </summary>
    private async Task<DeleteResult?> ResolveStoredRelationshipAsync(
        RelationalDeleteCommandRequest request,
        StoredRelationshipStatementPlan relationshipPlan,
        IReadOnlyList<RelationalCompositeStatementOutcome> outcomes,
        RelationalCompositeCapturedTarget capturedTarget,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        switch (relationshipPlan.Disposition)
        {
            case StoredRelationshipDisposition.None:
                return null;

            case StoredRelationshipDisposition.Standalone:
                return await ExecuteStandaloneRelationshipAsync(
                        request,
                        relationshipPlan.Authorized!,
                        capturedTarget.DocumentId,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

            case StoredRelationshipDisposition.DeferredNoClaims:
                return request.DeferredRelationshipDenial
                    ?? new DeleteResult.UnknownFailure(
                        "Relationship authorization required caller EducationOrganizationIds, but denial metadata could not be built."
                    );

            case StoredRelationshipDisposition.Unbuildable:
                return new DeleteResult.UnknownFailure(
                    "Relationship authorization produced executable checks without claim EducationOrganizationId parameterization."
                );

            case StoredRelationshipDisposition.Emitted:
                var row = (StoredRelationshipAuthorizationRow?)outcomes[relationshipPlan.Ordinal].Value;

                if (row is null)
                {
                    // The check's target CTE saw no row, which the capture lock makes unreachable. Kept as
                    // the same defensive mapping the standalone execution had.
                    return new DeleteResult.DeleteFailureNotExists();
                }

                return row.AuthorizationResult == 1
                    ? null
                    : new DeleteResult.DeleteFailureSecurityConfiguration(
                        [
                            $"Relationship authorization returned unexpected result '{row.AuthorizationResult}'.",
                        ],
                        AuthorizationSecurityConfigurationDiagnostics.ForRelationshipInvalidAuthorizationResult(
                            relationshipPlan.Authorized!.CheckSpecs
                        )
                    );

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(relationshipPlan),
                    relationshipPlan.Disposition,
                    null
                );
        }
    }

    /// <summary>
    /// Runs a relationship check the composite rewriter cannot rename — a table-valued claim binding — as
    /// its own ordered segment on the same session, against the decoded captured id. Authorization therefore
    /// still strictly precedes any deletion, at the cost of one more command on that path.
    /// </summary>
    private async Task<DeleteResult?> ExecuteStandaloneRelationshipAsync(
        RelationalDeleteCommandRequest request,
        RelationshipAuthorizationResult.Authorized authorized,
        long documentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var executionResult = await new SingleRecordRelationshipAuthorizationExecutor(
            writeSession.CreateCommandExecutor(),
            _relationalParameterConfigurator,
            _providerFailureExtractor,
            _logger
        )
            .ExecuteAsync(
                new SingleRecordRelationshipAuthorizationExecutionRequest(
                    request.MappingSet,
                    documentId,
                    authorized.CheckSpecs,
                    authorized.ClaimEducationOrganizationIdParameterization!,
                    RelationshipAuthorizationAuth1Index,
                    authorized.ExecutableShape
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return executionResult switch
        {
            SingleRecordRelationshipAuthorizationExecutionResult.Authorized => null,
            SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                new DeleteResult.DeleteFailureRelationshipNotAuthorized(notAuthorized.RelationshipFailure),
            SingleRecordRelationshipAuthorizationExecutionResult.StaleTarget =>
                new DeleteResult.DeleteFailureNotExists(),
            SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                new DeleteResult.DeleteFailureSecurityConfiguration(
                    [invalidFailure.FailureMessage],
                    invalidFailure.Diagnostics
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported single-record authorization execution result '{executionResult.GetType().Name}'."
            ),
        };
    }

    /// <summary>
    /// Maps a provider failure raised by the opening command: an AUTH1 denial first, then the same
    /// foreign-key, transient, and unknown-failure translation every relational delete has used.
    /// </summary>
    private DeleteResult MapProviderFailure(
        RelationalDeleteCommandRequest request,
        StoredNamespaceStatementPlan? namespacePlan,
        StoredRelationshipStatementPlan relationshipPlan,
        DbException exception
    ) =>
        RelationalCompositeStoredAuthorization.TryClassifyDenial(
            request.MappingSet.Key.Dialect,
            exception,
            namespacePlan,
            relationshipPlan,
            RelationshipAuthorizationAuth1Index,
            _providerFailureExtractor,
            _logger
        ) switch
        {
            // Stale is unreachable while the capture lock holds; kept as the same defensive mapping the
            // standalone execution had.
            StoredAuthorizationDenial.StaleTarget => new DeleteResult.DeleteFailureNotExists(),
            StoredAuthorizationDenial.NamespaceNotAuthorized(var failure) =>
                new DeleteResult.DeleteFailureNamespaceNotAuthorized(failure),
            StoredAuthorizationDenial.RelationshipNotAuthorized(var failure) =>
                new DeleteResult.DeleteFailureRelationshipNotAuthorized(failure),
            StoredAuthorizationDenial.SecurityConfiguration(var messages, var diagnostics) =>
                new DeleteResult.DeleteFailureSecurityConfiguration(messages, diagnostics),
            _ => RelationalDeleteExecution.MapFailure(
                exception,
                _exceptionClassifier,
                _deleteConstraintResolver,
                request.MappingSet.Model,
                _logger,
                request.DocumentUuid,
                request.TraceId,
                DeleteTargetKind.Document
            ),
        };

    /// <summary>
    /// Whether the <c>dms.Document</c> delete returned a row, which is how delete success is decided; the
    /// statement owns exactly one result set, so this never advances past it.
    /// </summary>
    private static async Task<object?> ReadDeletedDocumentRowAsync(
        DbDataReader reader,
        CancellationToken cancellationToken
    ) => await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
}

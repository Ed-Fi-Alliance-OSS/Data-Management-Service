// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Which statements the write path's second command carries.
/// </summary>
/// <remarks>
/// One rule decides both whether the command exists and what it contains: it is emitted if and only if
/// proposed authorization is configured or data-modifying statements are required, and its mode is
/// <see cref="Dml"/> if and only if data-modifying statements are required. Keeping both modes in one
/// implementation is what stops a no-DML situation from drifting out of alignment with the others.
/// </remarks>
internal enum RelationalWriteSecondCommandMode
{
    /// <summary>
    /// Exactly the proposed <c>AUTH1</c> statements the DML mode would carry, in the same order, and
    /// nothing else. Serves a guarded no-op, a deferred etag precondition failure, a deferred
    /// missing-document-reference failure, and any coincidence of them.
    /// </summary>
    AuthorizationOnly,

    /// <summary>
    /// The proposed <c>AUTH1</c> statements followed by the <c>dms.Document</c> row, the resource tables'
    /// deletes and upserts, and the committed <c>ContentVersion</c> read.
    /// </summary>
    Dml,
}

/// <summary>
/// What the second command decided: the merge result it may have enriched with the extracted proposed
/// relationship runtime check, the persisted target when DML ran, and an immediate result when
/// authorization denied or its plan could not be reconciled.
/// </summary>
internal sealed record RelationalWriteSecondCommandResolution(
    RelationalWriteMergeResult MergeResult,
    RelationalWritePersistResult? PersistResult,
    RelationalWriteExecutorResult? ImmediateResult
);

/// <summary>
/// Evaluates proposed-value authorization and, in <see cref="RelationalWriteSecondCommandMode.Dml"/>,
/// applies the request's data-modifying statements.
/// </summary>
/// <remarks>
/// A seam, so executor orchestration tests can substitute the sequential shape and keep asserting
/// precedence through a boundary, while production SQL construction, statement order, the ordered-segment
/// fallback, and failure mapping are owned by the composite implementation's own fixture.
/// </remarks>
internal interface IRelationalWriteSecondCommandPhase
{
    Task<RelationalWriteSecondCommandResolution> ResolveAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        RelationalWriteSecondCommandMode mode,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// The write path's second command: the proposed namespace <c>AUTH1</c> statement, the proposed
/// relationship <c>AUTH1</c> statement, and — in DML mode — the <c>dms.Document</c> row, the resource
/// tables' deletes and upserts, and the committed <c>ContentVersion</c> read, co-batched in that order.
/// </summary>
/// <remarks>
/// <para>
/// Statement order is precedence order. The command aborts at its first <c>AUTH1</c>, so emitting
/// namespace before relationship is what makes a namespace denial win over a concurrent relationship
/// denial — the same ordering the stored-value checks use in the first phase. Both authorization
/// statements precede the <c>dms.Document</c> insert, so a create's artifacts exist only once the proposed
/// values authorized; where a check cannot join the command at all, its ordered segment runs before the
/// data-modifying statements are built. The deferred dispositions that need no statement of their own (a
/// caller with no claims, a proposed plan that cannot be reconciled with the finalized root row) are held
/// back until the command has run, so a namespace denial still outranks them.
/// </para>
/// <para>
/// Proposed authorization cannot be hoisted into the first phase: its statements bind values taken from
/// the finalized merged root row, which does not exist until the first command's hydration result sets
/// are decoded and the merge runs. The two-command floor for an authorized write is therefore structural.
/// </para>
/// </remarks>
internal sealed class CompositeRelationalWriteSecondCommand(
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null,
    ILogger? logger = null,
    RelationalCommandBudget? commandBudget = null
) : IRelationalWriteSecondCommandPhase
{
    private const string ProposedNamespaceAuthorizationLabel = "proposed-namespace-authorization";
    private const string ProposedRelationshipAuthorizationLabel = "proposed-relationship-authorization";
    private const string DocumentInsertLabel = "document-insert";
    private const string ContentVersionReadLabel = "content-version-read";

    /// <summary>The <c>dms.Document</c> insert's bound <c>DocumentUuid</c> and <c>ResourceKeyId</c>.</summary>
    private const int DocumentInsertParameterCount = 2;

    /// <summary>
    /// Stands in for the created document id's subquery while a statement's parameter count is measured. Only
    /// the count matters there, and the real expression's parameter name is not allocated until emission.
    /// </summary>
    private const string ProbeDocumentIdExpression = "(SELECT 0)";

    private readonly IRelationalParameterConfigurator _relationalParameterConfigurator =
        relationalParameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;

    private readonly IRelationshipAuthorizationProviderFailureExtractor _providerFailureExtractor =
        relationshipAuthorizationProviderFailureExtractor
        ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    private readonly RelationalCommandBudget? _commandBudget = commandBudget;

    public async Task<RelationalWriteSecondCommandResolution> ResolveAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        RelationalWriteSecondCommandMode mode,
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
            return new RelationalWriteSecondCommandResolution(mergeResult, null, namespacePlanFailure);
        }

        var relationshipPlan = PlanRelationship(request, mergeResult);
        mergeResult = relationshipPlan.MergeResult;

        if (
            mode is RelationalWriteSecondCommandMode.AuthorizationOnly
            && namespacePlan is null
            && relationshipPlan.RuntimeCheck is null
        )
        {
            // Nothing to authorize and no DML to apply. A deferred disposition may still exist — a caller
            // with no claims — and it needs no command of its own.
            return new RelationalWriteSecondCommandResolution(
                mergeResult,
                null,
                relationshipPlan.DeferredResult
            );
        }

        var execution = await ExecuteAsync(
                request,
                mergeResult,
                mode,
                namespacePlan,
                relationshipPlan.RuntimeCheck,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new RelationalWriteSecondCommandResolution(
            mergeResult,
            execution.PersistResult,
            execution.ImmediateResult ?? relationshipPlan.DeferredResult
        );
    }

    private sealed record SecondCommandExecution(
        RelationalWritePersistResult? PersistResult,
        RelationalWriteExecutorResult? ImmediateResult
    );

    private async Task<SecondCommandExecution> ExecuteAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        RelationalWriteSecondCommandMode mode,
        NamespaceStatementPlan? namespacePlan,
        ProposedRelationshipAuthorizationRuntimeCheck? runtimeCheck,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var relationshipCommand = runtimeCheck is null
            ? null
            : ProposedRelationshipAuthorizationCommand.Build(
                request.MappingSet,
                request.WritePlan,
                runtimeCheck,
                _relationalParameterConfigurator
            );

        if (mode is RelationalWriteSecondCommandMode.Dml)
        {
            return await ExecuteDmlAsync(
                    request,
                    mergeResult,
                    namespacePlan,
                    runtimeCheck,
                    relationshipCommand,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var builder = CreateBuilder(request);
        var namespaceEmitted = TryAppendNamespace(builder, request, namespacePlan);
        var relationshipEmitted =
            relationshipCommand is not null
            && CanCoBatchRelationship(runtimeCheck!, relationshipCommand, Budget(request))
            && TryAppendRelationship(builder, relationshipCommand);

        if (namespaceEmitted || relationshipEmitted)
        {
            var run = await RunAsync(
                    request,
                    builder,
                    namespaceEmitted ? namespacePlan : null,
                    relationshipEmitted ? runtimeCheck : null,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (run.Denial is not null)
            {
                return new SecondCommandExecution(null, run.Denial);
            }
        }

        if (relationshipCommand is null || relationshipEmitted)
        {
            return new SecondCommandExecution(null, null);
        }

        // A structured claim parameterization or a combined parameter budget that does not fit selects
        // an ordered segment on the same session and transaction rather than a co-batched statement.
        return new SecondCommandExecution(
            null,
            await ExecuteStandaloneRelationshipAsync(
                    request,
                    runtimeCheck!,
                    relationshipCommand!,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false)
        );
    }

    /// <summary>
    /// Runs a DML write as the fewest commands the parameter budget allows: the proposed <c>AUTH1</c>
    /// statements, the <c>dms.Document</c> row, the resource tables' statements, and the committed
    /// <c>ContentVersion</c> read, packed in that order and never reordered.
    /// </summary>
    private async Task<SecondCommandExecution> ExecuteDmlAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        NamespaceStatementPlan? namespacePlan,
        ProposedRelationshipAuthorizationRuntimeCheck? runtimeCheck,
        RelationalCommand? relationshipCommand,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var budget = Budget(request);

        // The shared collection-key reservation has to precede the command that binds its values, so it is
        // the one statement of a DML write that cannot join them. It creates no artifact — a consumed
        // sequence value is not a row — so running it ahead of the authorization statements changes nothing
        // a denied request can observe.
        var preparation = await PrepareDmlAsync(request, mergeResult, writeSession, cancellationToken)
            .ConfigureAwait(false);

        if (
            relationshipCommand is not null
            && !CanCoBatchRelationship(runtimeCheck!, relationshipCommand, budget)
        )
        {
            // Create artifacts only after proposed authorization: a check that cannot join the command has
            // to run, and pass, before the data-modifying statements are built at all.
            if (namespacePlan is not null)
            {
                var namespaceOnlyBuilder = CreateBuilder(request);
                TryAppendNamespace(namespaceOnlyBuilder, request, namespacePlan);

                var namespaceRun = await RunAsync(
                        request,
                        namespaceOnlyBuilder,
                        namespacePlan,
                        runtimeCheck: null,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (namespaceRun.Denial is not null)
                {
                    return new SecondCommandExecution(null, namespaceRun.Denial);
                }
            }

            var standaloneDenial = await ExecuteStandaloneRelationshipAsync(
                    request,
                    runtimeCheck!,
                    relationshipCommand,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (standaloneDenial is not null)
            {
                return new SecondCommandExecution(null, standaloneDenial);
            }

            namespacePlan = null;
            relationshipCommand = null;
            runtimeCheck = null;
        }

        var units = BuildDmlUnits(request, preparation, namespacePlan, runtimeCheck, relationshipCommand);
        var packedCommands = RelationalCompositeCommandPacker.Pack(
            [.. units.Select(static unit => unit.PackUnit)],
            budget
        );
        var unitsByLabel = units.ToDictionary(static unit => unit.PackUnit.Label, StringComparer.Ordinal);
        List<IReadOnlyList<RelationalCompositeStatementOutcome>> outcomesByCommand = new(
            packedCommands.Count
        );
        DmlOutcomeLocations locations = new();

        for (var commandIndex = 0; commandIndex < packedCommands.Count; commandIndex++)
        {
            var builder = CreateBuilder(request);
            DmlEmitContext context = new(
                builder,
                // The derived document id binds one uuid parameter per command, so each command gets its own
                // carrier rather than reusing the previous command's issued name.
                DocumentIdCarrier.For(request.MappingSet.Key.Dialect, RequireTargetContext(request)),
                preparation.CollectionItemIdBindings,
                commandIndex,
                locations
            );

            foreach (var group in packedCommands[commandIndex])
            {
                unitsByLabel[group.Label].Emit(context, group.RowOffset, group.RowCount);
            }

            var run = await RunAsync(
                    request,
                    builder,
                    context.EmittedNamespacePlan,
                    context.EmittedRuntimeCheck,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (run.Denial is not null)
            {
                return new SecondCommandExecution(null, run.Denial);
            }

            outcomesByCommand.Add(run.Outcomes);
        }

        return new SecondCommandExecution(
            locations.Decode(request, RequireTargetContext(request), outcomesByCommand),
            null
        );
    }

    private RelationalCommandBudget Budget(RelationalWriteExecutorRequest request) =>
        _commandBudget ?? RelationalCommandBudget.ForDialect(request.MappingSet.Key.Dialect);

    private static RelationalWriteTargetContext RequireTargetContext(
        RelationalWriteExecutorRequest request
    ) =>
        request.TargetContext
        ?? throw new InvalidOperationException(
            "Relational DML persistence requires an executor-resolved target context."
        );

    private RelationalCompositeCommandBuilder CreateBuilder(RelationalWriteExecutorRequest request) =>
        new(IRelationalCompositeCommandDialect.Create(request.MappingSet.Key.Dialect), _commandBudget);

    private sealed record CommandRun(
        IReadOnlyList<RelationalCompositeStatementOutcome> Outcomes,
        RelationalWriteExecutorResult? Denial
    );

    /// <summary>
    /// Seals and runs the builder's command, mapping a provider <c>AUTH1</c> failure back to the denial the
    /// caller sees.
    /// </summary>
    private async Task<CommandRun> RunAsync(
        RelationalWriteExecutorRequest request,
        RelationalCompositeCommandBuilder builder,
        NamespaceStatementPlan? namespacePlan,
        ProposedRelationshipAuthorizationRuntimeCheck? runtimeCheck,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return new CommandRun(
                await new RelationalCompositeCommandExecution()
                    .ExecuteAsync(writeSession, builder.Seal(), cancellationToken)
                    .ConfigureAwait(false),
                null
            );
        }
        catch (DbException exception)
        {
            return new CommandRun(
                [],
                MapAuthorizationFailure(request, namespacePlan, runtimeCheck, exception)
            );
        }
    }

    /// <summary>
    /// Where in the emitted command stream the persisted target is carried, recorded as statements are
    /// emitted and read back once every command has run.
    /// </summary>
    private sealed class DmlOutcomeLocations
    {
        private (int CommandIndex, int Ordinal)? _documentInsert;
        private (int CommandIndex, int Ordinal)? _contentVersionRead;

        public void RecordDocumentInsert(int commandIndex, int ordinal) =>
            _documentInsert = (commandIndex, ordinal);

        public void RecordContentVersionRead(int commandIndex, int ordinal) =>
            _contentVersionRead = (commandIndex, ordinal);

        public RelationalWritePersistResult Decode(
            RelationalWriteExecutorRequest request,
            RelationalWriteTargetContext targetContext,
            IReadOnlyList<IReadOnlyList<RelationalCompositeStatementOutcome>> outcomesByCommand
        )
        {
            var documentId = targetContext switch
            {
                RelationalWriteTargetContext.ExistingDocument existing => existing.DocumentId,
                _ => RequireDocumentId(Value(_documentInsert, outcomesByCommand), request),
            };

            return new RelationalWritePersistResult(
                documentId,
                GetTargetDocumentUuid(targetContext),
                RequireContentVersion(Value(_contentVersionRead, outcomesByCommand), documentId)
            );
        }

        private static DocumentUuid GetTargetDocumentUuid(RelationalWriteTargetContext targetContext) =>
            targetContext switch
            {
                RelationalWriteTargetContext.CreateNew(var documentUuid) => documentUuid,
                RelationalWriteTargetContext.ExistingDocument(_, var documentUuid, _) => documentUuid,
                _ => throw new ArgumentOutOfRangeException(nameof(targetContext), targetContext, null),
            };

        private static object? Value(
            (int CommandIndex, int Ordinal)? location,
            IReadOnlyList<IReadOnlyList<RelationalCompositeStatementOutcome>> outcomesByCommand
        )
        {
            if (location is not { } resolved)
            {
                throw new InvalidOperationException(
                    "The DML command stream did not emit a statement the persisted target is decoded from."
                );
            }

            return outcomesByCommand[resolved.CommandIndex][resolved.Ordinal].Value;
        }

        private static long RequireDocumentId(object? value, RelationalWriteExecutorRequest request)
        {
            if (value is null or DBNull)
            {
                throw new InvalidOperationException(
                    "Document insert for resource "
                        + $"'{RelationalWriteSupport.FormatResource(request.WritePlan.Model.Resource)}' did "
                        + "not return a DocumentId."
                );
            }

            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private static long RequireContentVersion(object? value, long rootDocumentId)
        {
            if (value is null or DBNull)
            {
                throw new InvalidOperationException(
                    "Relational write persistence found no ContentVersion for committed document id "
                        + $"{rootDocumentId}."
                );
            }

            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// What one command's emission needs, plus which authorization plans it actually carried, so a provider
    /// failure is mapped only against the checks that command sent.
    /// </summary>
    private sealed class DmlEmitContext(
        RelationalCompositeCommandBuilder builder,
        DocumentIdCarrier documentId,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings,
        int commandIndex,
        DmlOutcomeLocations locations
    )
    {
        public RelationalCompositeCommandBuilder Builder => builder;

        public DocumentIdCarrier DocumentId => documentId;

        public RelationalWriteCollectionItemIdBindings CollectionItemIdBindings => collectionItemIdBindings;

        public int CommandIndex => commandIndex;

        public DmlOutcomeLocations Locations => locations;

        public NamespaceStatementPlan? EmittedNamespacePlan { get; set; }

        public ProposedRelationshipAuthorizationRuntimeCheck? EmittedRuntimeCheck { get; set; }
    }

    /// <summary>
    /// One unit of the DML command stream: what the packer needs in order to size it, and how to emit a row
    /// group of it into whichever command the packer placed that group in.
    /// </summary>
    private sealed record DmlEmitUnit(
        RelationalCompositePackUnit PackUnit,
        Action<DmlEmitContext, int, int> Emit
    );

    /// <summary>
    /// The statements a DML write owes, resolved and made bindable before any of them is emitted: the
    /// resolved statement order, and the collection keys reserved by the one command that had to precede
    /// this one.
    /// </summary>
    private sealed record DmlPreparation(
        RelationalWriteCollectionItemIdBindings CollectionItemIdBindings,
        RelationalWriteDmlStatementPlan Plan
    );

    private static async Task<DmlPreparation> PrepareDmlAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var dialect = request.MappingSet.Key.Dialect;
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

        return new DmlPreparation(collectionItemIdBindings, plan);
    }

    /// <summary>
    /// The units a DML write offers the packer, in the order the correctness invariants require: proposed
    /// authorization, the document row, the resource tables' resolved statement order, then the committed
    /// <c>ContentVersion</c> read.
    /// </summary>
    private static IReadOnlyList<DmlEmitUnit> BuildDmlUnits(
        RelationalWriteExecutorRequest request,
        DmlPreparation preparation,
        NamespaceStatementPlan? namespacePlan,
        ProposedRelationshipAuthorizationRuntimeCheck? runtimeCheck,
        RelationalCommand? relationshipCommand
    )
    {
        var targetContext = RequireTargetContext(request);
        var dialect = request.MappingSet.Key.Dialect;
        List<DmlEmitUnit> units = [];

        if (namespacePlan is not null)
        {
            units.Add(
                new DmlEmitUnit(
                    new RelationalCompositePackUnit(
                        ProposedNamespaceAuthorizationLabel,
                        RowCount: 0,
                        ParametersPerRow: 0,
                        FixedParameterCount: CountNamespaceParameters(request, namespacePlan)
                    ),
                    (context, _, _) =>
                    {
                        TryAppendNamespace(context.Builder, request, namespacePlan);
                        context.EmittedNamespacePlan = namespacePlan;
                    }
                )
            );
        }

        if (relationshipCommand is not null)
        {
            units.Add(
                new DmlEmitUnit(
                    new RelationalCompositePackUnit(
                        ProposedRelationshipAuthorizationLabel,
                        RowCount: 0,
                        ParametersPerRow: 0,
                        FixedParameterCount: relationshipCommand.Parameters.Count
                    ),
                    (context, _, _) =>
                    {
                        TryAppendRelationship(context.Builder, relationshipCommand);
                        context.EmittedRuntimeCheck = runtimeCheck;
                    }
                )
            );
        }

        if (targetContext is RelationalWriteTargetContext.CreateNew createNew)
        {
            units.Add(
                new DmlEmitUnit(
                    new RelationalCompositePackUnit(
                        DocumentInsertLabel,
                        RowCount: 0,
                        ParametersPerRow: 0,
                        FixedParameterCount: DocumentInsertParameterCount
                    ),
                    (context, _, _) =>
                        context.Locations.RecordDocumentInsert(
                            context.CommandIndex,
                            AppendDocumentInsert(context.Builder, request, createNew)
                        )
                )
            );
        }

        // A create's statements each need room for the command's single bound document uuid. Charging every
        // one of them for it over-counts by a parameter or two rather than risking a command that overflows.
        var derivedDocumentIdParameterCount = targetContext is RelationalWriteTargetContext.CreateNew ? 1 : 0;

        foreach (var statement in preparation.Plan.StatementsInOrder)
        {
            var capturedStatement = statement;

            units.Add(
                new DmlEmitUnit(
                    new RelationalCompositePackUnit(
                        statement.Label,
                        statement.Rows.Count,
                        MaxParametersPerRow(statement, targetContext, preparation.CollectionItemIdBindings),
                        derivedDocumentIdParameterCount
                    ),
                    (context, rowOffset, rowCount) =>
                        AppendDataModifyingStatement(
                            context.Builder,
                            capturedStatement,
                            rowOffset,
                            rowCount,
                            context.DocumentId,
                            context.CollectionItemIdBindings
                        )
                )
            );
        }

        units.Add(
            new DmlEmitUnit(
                new RelationalCompositePackUnit(
                    ContentVersionReadLabel,
                    RowCount: 0,
                    ParametersPerRow: 0,
                    FixedParameterCount: 1
                ),
                (context, _, _) =>
                    context.Locations.RecordContentVersionRead(
                        context.CommandIndex,
                        AppendContentVersionRead(context.Builder, dialect, context.DocumentId)
                    )
            )
        );

        return units;
    }

    /// <summary>
    /// The most parameters any one row of <paramref name="statement"/> binds. Measured from the statement's
    /// own SQL rather than predicted from the plan, because inlining and a narrowing statement both remove
    /// bindings; measuring the maximum keeps the packer from ever placing a group that will not fit.
    /// </summary>
    /// <remarks>
    /// One row is built to establish the shape. The only thing that can vary between rows of one statement
    /// is whether their collection key was inlined — the document id and the referenced binding set are
    /// properties of the table and the statement, not of the row — so a statement holding a mix of inlined
    /// and reserved keys is charged for the wider shape without building every row.
    /// </remarks>
    private static int MaxParametersPerRow(
        RelationalWriteDmlStatement statement,
        RelationalWriteTargetContext targetContext,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings
    )
    {
        RelationalWriteRootDocumentIdSource probeSource = targetContext
            is RelationalWriteTargetContext.ExistingDocument existing
            ? new RelationalWriteRootDocumentIdSource.Bound(existing.DocumentId)
            : new RelationalWriteRootDocumentIdSource.Derived(ProbeDocumentIdExpression);
        var parametersPerRow = DropUnreferencedParameters(
            RelationalWriteRowStatements.BuildRowCommand(
                statement.TableWritePlan,
                statement.SingleRowSql,
                statement.Rows[0],
                probeSource,
                collectionItemIdBindings
            )
        ).Parameters.Count;

        if (HasMixedCollectionKeyInlining(statement, collectionItemIdBindings))
        {
            parametersPerRow++;
        }

        // A statement whose every binding is inlined or unreferenced still occupies a row group, and the
        // packer rejects a unit with rows but no parameters per row.
        return Math.Max(1, parametersPerRow);
    }

    /// <summary>
    /// Whether the rows of one statement disagree about whether their collection key was inlined, which is
    /// the only thing that can make one row of a statement bind more parameters than another.
    /// </summary>
    private static bool HasMixedCollectionKeyInlining(
        RelationalWriteDmlStatement statement,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings
    )
    {
        if (statement.TableWritePlan.CollectionKeyPreallocationPlan is not { } preallocationPlan)
        {
            return false;
        }

        var inlinedInFirstRow = IsInlinedKey(statement.Rows[0]);

        return statement.Rows.Any(row => IsInlinedKey(row) != inlinedInFirstRow);

        bool IsInlinedKey(RelationalWriteMergedTableRow row) =>
            row.Values[preallocationPlan.BindingIndex] is FlattenedWriteValue.UnresolvedCollectionItemId token
            && collectionItemIdBindings.IsInlined(token);
    }

    private static int CountNamespaceParameters(
        RelationalWriteExecutorRequest request,
        NamespaceStatementPlan statementPlan
    ) => BuildNamespaceCommand(request, statementPlan).Parameters.Count;

    private static int AppendDocumentInsert(
        RelationalCompositeCommandBuilder builder,
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.CreateNew createNew
    )
    {
        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            RelationalDocumentRowCommandBuilder.BuildInsertCommand(
                request.MappingSet.Key.Dialect,
                createNew.DocumentUuid,
                RelationalWriteSupport.GetResourceKeyIdOrThrow(
                    request.MappingSet,
                    request.WritePlan.Model.Resource
                )
            ),
            builder.Allocator,
            builder.NextOrdinal
        );

        return builder.Append(
            DocumentInsertLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Scalar
        );
    }

    private static void AppendDataModifyingStatement(
        RelationalCompositeCommandBuilder builder,
        RelationalWriteDmlStatement statement,
        int rowOffset,
        int rowCount,
        DocumentIdCarrier documentId,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings
    )
    {
        var statementDocumentId = documentId.ForStatement(builder);
        var command =
            rowCount == 1
                ? RelationalWriteRowStatements.BuildRowCommand(
                    statement.TableWritePlan,
                    statement.SingleRowSql,
                    statement.Rows[rowOffset],
                    statementDocumentId.Source,
                    collectionItemIdBindings
                )
                : RelationalWriteRowStatements.BuildBatchCommand(
                    statement.EmitBatchSql(rowCount),
                    statement.TableWritePlan,
                    statement.Rows,
                    rowOffset,
                    rowCount,
                    statementDocumentId.Source,
                    collectionItemIdBindings
                );
        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            DropUnreferencedParameters(command),
            builder.Allocator,
            builder.NextOrdinal,
            statementDocumentId.Substitutions
        );

        builder.Append(
            statement.Label,
            rewritten.Sql,
            documentId.CombineBinding(rewritten.Sql, rewritten.Parameters),
            RelationalCompositeResultShape.Sentinel
        );
    }

    /// <summary>
    /// Drops the bindings a statement narrowing to a key never mentions — a delete's non-key columns, an
    /// update's untouched ones.
    /// </summary>
    /// <remarks>
    /// The per-statement path can bind them harmlessly, but a co-batched command cannot: the composite
    /// rewriter treats a declared parameter the SQL never references as evidence that the standalone builder
    /// changed shape underneath it, which it must, because it cannot otherwise tell drift from a statement
    /// that simply narrows. Dropping them here keeps that guard strict and stops a delete from spending
    /// another table's worth of the command's parameter budget.
    /// </remarks>
    private static RelationalCommand DropUnreferencedParameters(RelationalCommand command)
    {
        var referencedNames = RelationalParameterTokenRewriter.CollectParameterNames(command.CommandText);
        var referencedParameters = command
            .Parameters.Where(parameter =>
                referencedNames.Contains(RelationalParameterTokenRewriter.BareName(parameter.Name))
            )
            .ToArray();

        return referencedParameters.Length == command.Parameters.Count
            ? command
            : new RelationalCommand(command.CommandText, referencedParameters);
    }

    private static int AppendContentVersionRead(
        RelationalCompositeCommandBuilder builder,
        SqlDialect dialect,
        DocumentIdCarrier documentId
    )
    {
        var statementDocumentId = documentId.ForStatement(builder);
        var command = statementDocumentId.Source switch
        {
            RelationalWriteRootDocumentIdSource.Bound bound =>
                RelationalDocumentLockCommandBuilder.BuildContentVersionCommand(dialect, bound.DocumentId),
            RelationalWriteRootDocumentIdSource.Derived derived =>
                RelationalDocumentLockCommandBuilder.BuildContentVersionCommand(dialect, derived.Sql),
            _ => throw new ArgumentOutOfRangeException(nameof(documentId), statementDocumentId.Source, null),
        };
        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            command,
            builder.Allocator,
            builder.NextOrdinal,
            statementDocumentId.Substitutions
        );

        return builder.Append(
            ContentVersionReadLabel,
            rewritten.Sql,
            documentId.CombineBinding(rewritten.Sql, rewritten.Parameters),
            RelationalCompositeResultShape.Scalar
        );
    }

    /// <summary>
    /// What one statement needs in order to reach the root document id: the source the row builder emits and
    /// the substitution that lets the composite rewriter explain the derived expression's bind marker.
    /// </summary>
    private sealed record StatementDocumentId(
        RelationalWriteRootDocumentIdSource Source,
        IReadOnlyDictionary<string, string>? Substitutions
    );

    /// <summary>
    /// Supplies the root document id to every statement of one composite command: the value the first
    /// phase already decoded for an existing target, or a scalar subquery on the unique
    /// <c>DocumentUuid</c> for a create whose identity <c>dms.Document</c> generates inside this command.
    /// </summary>
    /// <remarks>
    /// The derived form binds the uuid exactly once per command. The name is allocated on first use, so it
    /// is bound by a statement whose own SQL references it, and every later statement reuses that name
    /// rather than binding another copy of the same value.
    /// </remarks>
    private sealed class DocumentIdCarrier
    {
        private const string CreatedDocumentUuidParameterBaseName = "dmsCreatedDocumentUuid";

        private readonly SqlDialect _dialect;
        private readonly Guid _createdDocumentUuid;

        private string? _issuedUuidParameterName;
        private bool _bound;

        private DocumentIdCarrier(SqlDialect dialect, long? knownDocumentId, Guid createdDocumentUuid)
        {
            _dialect = dialect;
            _createdDocumentUuid = createdDocumentUuid;
            KnownDocumentId = knownDocumentId;
        }

        /// <summary>The already-known document id, or <see langword="null"/> when this command creates it.</summary>
        public long? KnownDocumentId { get; }

        public static DocumentIdCarrier For(SqlDialect dialect, RelationalWriteTargetContext targetContext) =>
            targetContext switch
            {
                RelationalWriteTargetContext.ExistingDocument existing => new DocumentIdCarrier(
                    dialect,
                    existing.DocumentId,
                    Guid.Empty
                ),
                RelationalWriteTargetContext.CreateNew createNew => new DocumentIdCarrier(
                    dialect,
                    null,
                    createNew.DocumentUuid.Value
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(targetContext), targetContext, null),
            };

        public StatementDocumentId ForStatement(RelationalCompositeCommandBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            if (KnownDocumentId is { } knownDocumentId)
            {
                return new StatementDocumentId(
                    new RelationalWriteRootDocumentIdSource.Bound(knownDocumentId),
                    null
                );
            }

            _issuedUuidParameterName ??= builder.Allocator.AllocateStatementScoped(
                CreatedDocumentUuidParameterBaseName,
                builder.NextOrdinal
            );

            return new StatementDocumentId(
                new RelationalWriteRootDocumentIdSource.Derived(
                    RelationalDocumentRowCommandBuilder.BuildDocumentIdSubquery(
                        _dialect,
                        _issuedUuidParameterName
                    )
                ),
                // The allocator already issued the final name, so the rewriter needs an identity
                // substitution rather than a rename: without it the token would look undeclared.
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [RelationalParameterTokenRewriter.BareName(_issuedUuidParameterName)] =
                        _issuedUuidParameterName,
                }
            );
        }

        /// <summary>
        /// Adds the command's single created-document-uuid parameter to the first statement whose emitted SQL
        /// actually references it. Binding it on a statement that does not would leave the parameter
        /// unreferenced by its own statement, and binding it on every statement would bind the same value
        /// repeatedly under different names.
        /// </summary>
        public IReadOnlyList<RelationalParameter> CombineBinding(
            string sql,
            IReadOnlyList<RelationalParameter> parameters
        )
        {
            if (
                _bound
                || _issuedUuidParameterName is null
                || !sql.Contains(_issuedUuidParameterName, StringComparison.OrdinalIgnoreCase)
            )
            {
                return parameters;
            }

            _bound = true;

            return [.. parameters, new RelationalParameter(_issuedUuidParameterName, _createdDocumentUuid)];
        }
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
        RelationalCommandBudget budget
    ) =>
        runtimeCheck.ClaimEducationOrganizationIdParameterization.Kind
            is not AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured
        && relationshipCommand.Parameters.Count <= budget.MaxParametersPerCommand;

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

    private static RelationalCommand BuildNamespaceCommand(
        RelationalWriteExecutorRequest request,
        NamespaceStatementPlan statementPlan
    )
    {
        var sqlPlan = new NamespaceAuthorizationSqlCompiler(request.MappingSet.Key.Dialect).Compile(
            new NamespaceAuthorizationSqlSpec(
                statementPlan.Checks,
                statementPlan.PrefixParameterization,
                NamespaceAuthorizationSqlSpecDefaults.DocumentIdParameterName,
                NamespaceAuthorizationSqlSpecDefaults.ProposedNamespaceParameterName
            )
        );

        return NamespaceAuthorizationExecutor.BuildCommand(
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

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            BuildNamespaceCommand(request, statementPlan),
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

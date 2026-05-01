// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Classifies the request-side relational write entrypoint.
/// </summary>
public enum RelationalWriteOperationKind
{
    /// <summary>
    /// A POST upsert write.
    /// </summary>
    Post,

    /// <summary>
    /// A PUT-by-id write.
    /// </summary>
    Put,
}

/// <summary>
/// The resolved target document context for a relational write.
/// </summary>
public abstract record RelationalWriteTargetContext
{
    /// <summary>
    /// The write targets a document that does not yet have a persisted <c>DocumentId</c>.
    /// </summary>
    /// <param name="DocumentUuid">The externally visible document id reserved by the caller.</param>
    public sealed record CreateNew(DocumentUuid DocumentUuid) : RelationalWriteTargetContext;

    /// <summary>
    /// The write targets an already persisted document.
    /// </summary>
    /// <param name="DocumentId">The persisted relational document id.</param>
    /// <param name="DocumentUuid">The persisted externally visible document id.</param>
    /// <param name="ObservedContentVersion">
    /// The stored representation stamp observed during target lookup for this existing document.
    /// </param>
    public sealed record ExistingDocument(
        long DocumentId,
        DocumentUuid DocumentUuid,
        long ObservedContentVersion = 0
    ) : RelationalWriteTargetContext;
}

/// <summary>
/// Repository-facing target selection inputs retained alongside the resolved executor target context.
/// </summary>
public abstract record RelationalWriteTargetRequest
{
    /// <summary>
    /// POST may create a brand-new document or update an existing document resolved by referential id.
    /// </summary>
    /// <param name="ReferentialId">The natural-identity lookup key for POST upsert semantics.</param>
    /// <param name="CandidateDocumentUuid">
    /// The caller-reserved document uuid to use when lookup resolves to a new document.
    /// </param>
    public sealed record Post(ReferentialId ReferentialId, DocumentUuid CandidateDocumentUuid)
        : RelationalWriteTargetRequest;

    /// <summary>
    /// PUT must resolve an already persisted document by external document uuid.
    /// </summary>
    /// <param name="DocumentUuid">The externally visible document id addressed by the caller.</param>
    public sealed record Put(DocumentUuid DocumentUuid) : RelationalWriteTargetRequest;
}

/// <summary>
/// Operation-correct relational write lookup result before translation to executor-facing target context.
/// </summary>
public abstract record RelationalWriteTargetLookupResult
{
    /// <summary>
    /// POST resolved to a brand-new document.
    /// </summary>
    /// <param name="DocumentUuid">The externally visible document id reserved by the caller.</param>
    public sealed record CreateNew(DocumentUuid DocumentUuid) : RelationalWriteTargetLookupResult;

    /// <summary>
    /// POST or PUT resolved to an already persisted document.
    /// </summary>
    /// <param name="DocumentId">The persisted relational document id.</param>
    /// <param name="DocumentUuid">The persisted externally visible document id.</param>
    /// <param name="ObservedContentVersion">The stored representation stamp observed during lookup.</param>
    public sealed record ExistingDocument(
        long DocumentId,
        DocumentUuid DocumentUuid,
        long ObservedContentVersion
    ) : RelationalWriteTargetLookupResult;

    /// <summary>
    /// PUT did not resolve to a persisted document.
    /// </summary>
    public sealed record NotFound : RelationalWriteTargetLookupResult;
}

/// <summary>
/// The committed root document identity returned by a successful persistence pass.
/// </summary>
internal sealed record RelationalWritePersistResult(long DocumentId, DocumentUuid DocumentUuid);

/// <summary>
/// Backend-local input contract for flattening a validated write body into relational buffers and candidates.
/// </summary>
public sealed record FlatteningInput
{
    public FlatteningInput(
        RelationalWriteOperationKind operationKind,
        RelationalWriteTargetContext targetContext,
        ResourceWritePlan writePlan,
        JsonNode selectedBody,
        ResolvedReferenceSet resolvedReferences,
        bool emitEmptyRootExtensionBuffers = false
    )
    {
        OperationKind = operationKind;
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        WritePlan = writePlan ?? throw new ArgumentNullException(nameof(writePlan));
        SelectedBody = selectedBody ?? throw new ArgumentNullException(nameof(selectedBody));
        ResolvedReferences =
            resolvedReferences ?? throw new ArgumentNullException(nameof(resolvedReferences));
        EmitEmptyRootExtensionBuffers = emitEmptyRootExtensionBuffers;
    }

    /// <summary>
    /// The write entrypoint that selected this flattening pass.
    /// </summary>
    public RelationalWriteOperationKind OperationKind { get; init; }

    /// <summary>
    /// The resolved target document context for the write.
    /// </summary>
    public RelationalWriteTargetContext TargetContext { get; init; }

    /// <summary>
    /// The compiled write plan for the resource being flattened.
    /// </summary>
    public ResourceWritePlan WritePlan { get; init; }

    /// <summary>
    /// The caller-selected body to flatten.
    /// </summary>
    public JsonNode SelectedBody { get; init; }

    /// <summary>
    /// The request-local resolved references used for FK population.
    /// </summary>
    public ResolvedReferenceSet ResolvedReferences { get; init; }

    /// <summary>
    /// When true, the flattener emits a <see cref="RootExtensionWriteRowBuffer"/> for every
    /// root-extension scope and a <see cref="CandidateAttachedAlignedScopeData"/> for every
    /// collection-aligned extension scope whose node is present as a JSON object in the
    /// selected body, even when the object carries no bound scalar data and no collection
    /// candidates. The profile-aware executor sets this so a profile-shaped body like
    /// <c>_ext: { sample: {} }</c> (e.g. an explicitly empty visible scope) still produces a
    /// buffer for the profile merge synthesizer to overlay, rather than being silently
    /// dropped under the default "no bound data" heuristic. Non-profile callers leave it off
    /// to preserve the historical drop-empty behavior.
    /// </summary>
    public bool EmitEmptyRootExtensionBuffers { get; init; }
}

/// <summary>
/// A flattened relational write tree rooted at the resource root row.
/// </summary>
public sealed record FlattenedWriteSet
{
    public FlattenedWriteSet(RootWriteRowBuffer rootRow)
    {
        RootRow = rootRow ?? throw new ArgumentNullException(nameof(rootRow));
    }

    /// <summary>
    /// The root table row plus all attached root-extension rows and collection candidates.
    /// </summary>
    public RootWriteRowBuffer RootRow { get; init; }
}

/// <summary>
/// One flattened value in authoritative <see cref="TableWritePlan.ColumnBindings" /> order.
/// </summary>
public abstract record FlattenedWriteValue
{
    /// <summary>
    /// A materialized scalar, FK, ordinal, or already-resolved key value.
    /// </summary>
    public sealed record Literal(object? Value) : FlattenedWriteValue;

    /// <summary>
    /// The root <c>DocumentId</c> remains unresolved and will be supplied by a later write stage.
    /// </summary>
    public sealed record UnresolvedRootDocumentId : FlattenedWriteValue
    {
        private UnresolvedRootDocumentId() { }

        public static UnresolvedRootDocumentId Instance { get; } = new();
    }

    /// <summary>
    /// A stable <c>CollectionItemId</c> remains unresolved and will be bound by a later write stage.
    /// </summary>
    public sealed record UnresolvedCollectionItemId(Guid Token) : FlattenedWriteValue
    {
        public static UnresolvedCollectionItemId Create() => new(Guid.NewGuid());
    }
}

/// <summary>
/// The root table row plus any directly attached root-extension rows and collection candidates.
/// </summary>
public sealed record RootWriteRowBuffer
{
    public RootWriteRowBuffer(
        TableWritePlan tableWritePlan,
        IEnumerable<FlattenedWriteValue> values,
        IEnumerable<RootExtensionWriteRowBuffer>? rootExtensionRows = null,
        IEnumerable<CollectionWriteCandidate>? collectionCandidates = null
    )
    {
        TableWritePlan = tableWritePlan ?? throw new ArgumentNullException(nameof(tableWritePlan));
        Values = FlattenedWriteContractSupport.ToImmutableArray(values, nameof(values));
        RootExtensionRows = FlattenedWriteContractSupport.ToImmutableArray(
            rootExtensionRows ?? [],
            nameof(rootExtensionRows)
        );
        CollectionCandidates = FlattenedWriteContractSupport.ToImmutableArray(
            collectionCandidates ?? [],
            nameof(collectionCandidates)
        );

        FlattenedWriteContractSupport.ValidateBindingCount(TableWritePlan, Values, nameof(values));
        FlattenedWriteContractSupport.ValidateTableKind(
            TableWritePlan,
            DbTableKind.Root,
            nameof(tableWritePlan)
        );
    }

    /// <summary>
    /// The compiled table plan whose binding order this row buffer follows.
    /// </summary>
    public TableWritePlan TableWritePlan { get; init; }

    /// <summary>
    /// Row values in authoritative <see cref="TableWritePlan.ColumnBindings" /> order.
    /// </summary>
    public ImmutableArray<FlattenedWriteValue> Values { get; init; }

    /// <summary>
    /// Root extension rows nested directly under the resource root.
    /// </summary>
    public ImmutableArray<RootExtensionWriteRowBuffer> RootExtensionRows { get; init; }

    /// <summary>
    /// Collection candidates nested directly under the resource root.
    /// </summary>
    public ImmutableArray<CollectionWriteCandidate> CollectionCandidates { get; init; }
}

/// <summary>
/// A root-scope extension row buffer plus any extension child collections under that extension site.
/// </summary>
public sealed record RootExtensionWriteRowBuffer
{
    public RootExtensionWriteRowBuffer(
        TableWritePlan tableWritePlan,
        IEnumerable<FlattenedWriteValue> values,
        IEnumerable<CollectionWriteCandidate>? collectionCandidates = null
    )
    {
        TableWritePlan = tableWritePlan ?? throw new ArgumentNullException(nameof(tableWritePlan));
        Values = FlattenedWriteContractSupport.ToImmutableArray(values, nameof(values));
        CollectionCandidates = FlattenedWriteContractSupport.ToImmutableArray(
            collectionCandidates ?? [],
            nameof(collectionCandidates)
        );

        FlattenedWriteContractSupport.ValidateBindingCount(TableWritePlan, Values, nameof(values));

        var tableKind = TableWritePlan.TableModel.IdentityMetadata.TableKind;

        if (tableKind is DbTableKind.RootExtension)
        {
            return;
        }

        throw new ArgumentException(
            $"{nameof(RootExtensionWriteRowBuffer)} requires a table kind of "
                + $"{nameof(DbTableKind.RootExtension)}. Actual value: {tableKind}.",
            nameof(tableWritePlan)
        );
    }

    /// <summary>
    /// The compiled table plan whose binding order this row buffer follows.
    /// </summary>
    public TableWritePlan TableWritePlan { get; init; }

    /// <summary>
    /// Row values in authoritative <see cref="TableWritePlan.ColumnBindings" /> order.
    /// </summary>
    public ImmutableArray<FlattenedWriteValue> Values { get; init; }

    /// <summary>
    /// Collection candidates nested directly under this root extension scope.
    /// </summary>
    public ImmutableArray<CollectionWriteCandidate> CollectionCandidates { get; init; }
}

/// <summary>
/// A persisted collection-row candidate plus any nested collections and aligned extension scopes that belong to it.
/// </summary>
public sealed record CollectionWriteCandidate
{
    public CollectionWriteCandidate(
        TableWritePlan tableWritePlan,
        IEnumerable<int> ordinalPath,
        int requestOrder,
        IEnumerable<FlattenedWriteValue> values,
        IEnumerable<object?> semanticIdentityValues,
        IEnumerable<CandidateAttachedAlignedScopeData>? attachedAlignedScopeData = null,
        IEnumerable<CollectionWriteCandidate>? collectionCandidates = null,
        IEnumerable<SemanticIdentityPart>? semanticIdentityInOrder = null
    )
    {
        TableWritePlan = tableWritePlan ?? throw new ArgumentNullException(nameof(tableWritePlan));
        OrdinalPath = FlattenedWriteContractSupport.ToImmutableArray(ordinalPath, nameof(ordinalPath));
        Values = FlattenedWriteContractSupport.ToImmutableArray(values, nameof(values));
        SemanticIdentityValues = FlattenedWriteContractSupport.ToImmutableArray(
            semanticIdentityValues,
            nameof(semanticIdentityValues)
        );
        AttachedAlignedScopeData = FlattenedWriteContractSupport.ToImmutableArray(
            attachedAlignedScopeData ?? [],
            nameof(attachedAlignedScopeData)
        );
        CollectionCandidates = FlattenedWriteContractSupport.ToImmutableArray(
            collectionCandidates ?? [],
            nameof(collectionCandidates)
        );
        RequestOrder = requestOrder;

        ArgumentOutOfRangeException.ThrowIfNegative(requestOrder);

        if (OrdinalPath.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                $"{nameof(ordinalPath)} must contain at least one array ordinal.",
                nameof(ordinalPath)
            );
        }

        var tableKind = TableWritePlan.TableModel.IdentityMetadata.TableKind;

        if (tableKind is not (DbTableKind.Collection or DbTableKind.ExtensionCollection))
        {
            throw new ArgumentException(
                $"{nameof(CollectionWriteCandidate)} requires a table kind of "
                    + $"{nameof(DbTableKind.Collection)} or {nameof(DbTableKind.ExtensionCollection)}. "
                    + $"Actual value: {tableKind}.",
                nameof(tableWritePlan)
            );
        }

        FlattenedWriteContractSupport.ValidateBindingCount(TableWritePlan, Values, nameof(values));

        var mergePlan = TableWritePlan.CollectionMergePlan;

        if (mergePlan is null)
        {
            throw new ArgumentException(
                $"{nameof(CollectionWriteCandidate)} requires {nameof(TableWritePlan.CollectionMergePlan)} "
                    + "to be present.",
                nameof(tableWritePlan)
            );
        }

        if (SemanticIdentityValues.Length != mergePlan.SemanticIdentityBindings.Length)
        {
            throw new ArgumentException(
                $"{nameof(semanticIdentityValues)} must contain one entry per compiled semantic identity binding. "
                    + $"Expected {mergePlan.SemanticIdentityBindings.Length}, actual {SemanticIdentityValues.Length}.",
                nameof(semanticIdentityValues)
            );
        }

        SemanticIdentityInOrder = semanticIdentityInOrder is not null
            ? FlattenedWriteContractSupport.ToImmutableArray(
                semanticIdentityInOrder,
                nameof(semanticIdentityInOrder)
            )
            : DeriveSemanticIdentityInOrderFromValues(SemanticIdentityValues, mergePlan);

        if (SemanticIdentityInOrder.Length != mergePlan.SemanticIdentityBindings.Length)
        {
            throw new ArgumentException(
                $"{nameof(semanticIdentityInOrder)} must contain one entry per compiled semantic identity binding. "
                    + $"Expected {mergePlan.SemanticIdentityBindings.Length}, actual {SemanticIdentityInOrder.Length}.",
                nameof(semanticIdentityInOrder)
            );
        }
    }

    private static ImmutableArray<SemanticIdentityPart> DeriveSemanticIdentityInOrderFromValues(
        ImmutableArray<object?> values,
        CollectionMergePlan mergePlan
    )
    {
        // Legacy fallback used when a caller does not supply presence-aware identity. Treats
        // each part as <c>IsPresent: value is not null</c>, which preserves the historical
        // shape but collapses missing-vs-explicit-null. Production callers (the flattener)
        // must supply <see cref="SemanticIdentityPart"/> with explicit presence; this branch
        // exists for in-memory test builders that did not yet adopt the presence-aware path.
        var bindings = mergePlan.SemanticIdentityBindings;
        var parts = new SemanticIdentityPart[bindings.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            var rawValue = values[i];
            JsonNode? jsonValue = rawValue is null ? null : JsonValue.Create(rawValue);
            parts[i] = new SemanticIdentityPart(
                bindings[i].RelativePath.Canonical,
                jsonValue,
                IsPresent: rawValue is not null
            );
        }
        return [.. parts];
    }

    /// <summary>
    /// The compiled table plan whose binding order this candidate follows.
    /// </summary>
    public TableWritePlan TableWritePlan { get; init; }

    /// <summary>
    /// The request array ordinal path that identified this candidate during traversal.
    /// </summary>
    public ImmutableArray<int> OrdinalPath { get; init; }

    /// <summary>
    /// The sibling order emitted by the request traversal for the candidate's immediate scope.
    /// </summary>
    public int RequestOrder { get; init; }

    /// <summary>
    /// Candidate values in authoritative <see cref="TableWritePlan.ColumnBindings" /> order.
    /// </summary>
    public ImmutableArray<FlattenedWriteValue> Values { get; init; }

    /// <summary>
    /// The compiled semantic-identity values in deterministic binding order.
    /// </summary>
    public ImmutableArray<object?> SemanticIdentityValues { get; init; }

    /// <summary>
    /// The compiled semantic identity in <see cref="SemanticIdentityPart"/> form, parallel
    /// to <see cref="SemanticIdentityValues"/>. Each entry pairs the binding's relative path
    /// with the materialized JSON value and a presence flag that distinguishes a missing
    /// property from an explicit JSON null. Production candidates produced by the flattener
    /// supply this directly; legacy in-memory builders that pass only
    /// <see cref="SemanticIdentityValues"/> get a fallback whose <c>IsPresent</c> is derived
    /// as <c>value is not null</c>.
    /// </summary>
    public ImmutableArray<SemanticIdentityPart> SemanticIdentityInOrder { get; init; }

    /// <summary>
    /// Collection-aligned one-to-one scopes that remain attached to the owning collection candidate.
    /// </summary>
    public ImmutableArray<CandidateAttachedAlignedScopeData> AttachedAlignedScopeData { get; init; }

    /// <summary>
    /// Nested collection candidates that hang directly from this collection scope.
    /// </summary>
    public ImmutableArray<CollectionWriteCandidate> CollectionCandidates { get; init; }
}

/// <summary>
/// One collection-aligned one-to-one scope that remains attached to its owning collection candidate.
/// </summary>
public sealed record CandidateAttachedAlignedScopeData
{
    public CandidateAttachedAlignedScopeData(
        TableWritePlan tableWritePlan,
        IEnumerable<FlattenedWriteValue> values,
        IEnumerable<CollectionWriteCandidate>? collectionCandidates = null
    )
    {
        TableWritePlan = tableWritePlan ?? throw new ArgumentNullException(nameof(tableWritePlan));
        Values = FlattenedWriteContractSupport.ToImmutableArray(values, nameof(values));
        CollectionCandidates = FlattenedWriteContractSupport.ToImmutableArray(
            collectionCandidates ?? [],
            nameof(collectionCandidates)
        );

        FlattenedWriteContractSupport.ValidateTableKind(
            TableWritePlan,
            DbTableKind.CollectionExtensionScope,
            nameof(tableWritePlan)
        );
        FlattenedWriteContractSupport.ValidateBindingCount(TableWritePlan, Values, nameof(values));
    }

    /// <summary>
    /// The compiled table plan for the aligned one-to-one scope.
    /// </summary>
    public TableWritePlan TableWritePlan { get; init; }

    /// <summary>
    /// Row values in authoritative <see cref="TableWritePlan.ColumnBindings" /> order.
    /// </summary>
    public ImmutableArray<FlattenedWriteValue> Values { get; init; }

    /// <summary>
    /// Extension child collections nested under this aligned scope.
    /// </summary>
    public ImmutableArray<CollectionWriteCandidate> CollectionCandidates { get; init; }
}

/// <summary>
/// Input contract for executor-owned relational write orchestration.
/// </summary>
public sealed record RelationalWriteExecutorRequest
{
    public RelationalWriteExecutorRequest(
        MappingSet mappingSet,
        RelationalWriteOperationKind operationKind,
        RelationalWriteTargetRequest targetRequest,
        ResourceWritePlan writePlan,
        ResourceReadPlan? existingDocumentReadPlan,
        JsonNode selectedBody,
        bool allowIdentityUpdates,
        TraceId traceId,
        ReferenceResolverRequest referenceResolutionRequest,
        RelationalWriteTargetContext targetContext,
        BackendProfileWriteContext? profileWriteContext = null
    )
    {
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        OperationKind = operationKind;
        TargetRequest = targetRequest ?? throw new ArgumentNullException(nameof(targetRequest));
        WritePlan = writePlan ?? throw new ArgumentNullException(nameof(writePlan));
        ExistingDocumentReadPlan = existingDocumentReadPlan;
        SelectedBody = selectedBody ?? throw new ArgumentNullException(nameof(selectedBody));
        AllowIdentityUpdates = allowIdentityUpdates;
        TraceId = traceId;
        ReferenceResolutionRequest =
            referenceResolutionRequest ?? throw new ArgumentNullException(nameof(referenceResolutionRequest));
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));

        if (
            (OperationKind, TargetRequest)
            is not
                (RelationalWriteOperationKind.Post, RelationalWriteTargetRequest.Post)
                and not
                (RelationalWriteOperationKind.Put, RelationalWriteTargetRequest.Put)
        )
        {
            throw new ArgumentException(
                $"{nameof(targetRequest)} must match relational write operation '{OperationKind}'.",
                nameof(targetRequest)
            );
        }

        if (
            TargetContext is RelationalWriteTargetContext.CreateNew
            && OperationKind != RelationalWriteOperationKind.Post
        )
        {
            throw new ArgumentException(
                $"{nameof(targetContext)} cannot be CreateNew for relational write operation '{OperationKind}'.",
                nameof(targetContext)
            );
        }

        if (
            ExistingDocumentReadPlan is not null
            && ExistingDocumentReadPlan.Model.Resource != WritePlan.Model.Resource
        )
        {
            throw new ArgumentException(
                $"{nameof(existingDocumentReadPlan)} must target resource '{RelationalWriteSupport.FormatResource(WritePlan.Model.Resource)}'.",
                nameof(existingDocumentReadPlan)
            );
        }

        if (!ReferenceEquals(MappingSet, ReferenceResolutionRequest.MappingSet))
        {
            throw new ArgumentException(
                $"{nameof(referenceResolutionRequest)} must reference the same mapping set instance supplied to the executor request.",
                nameof(referenceResolutionRequest)
            );
        }

        if (ReferenceResolutionRequest.RequestResource != WritePlan.Model.Resource)
        {
            throw new ArgumentException(
                $"{nameof(referenceResolutionRequest)} must target resource '{RelationalWriteSupport.FormatResource(WritePlan.Model.Resource)}'.",
                nameof(referenceResolutionRequest)
            );
        }

        ProfileWriteContext = profileWriteContext;
    }

    /// <summary>
    /// The active mapping set selected for the current request.
    /// </summary>
    public MappingSet MappingSet { get; init; }

    /// <summary>
    /// The write entrypoint that initiated executor orchestration.
    /// </summary>
    public RelationalWriteOperationKind OperationKind { get; init; }

    /// <summary>
    /// The original request-side lookup input retained for retry, freshness, and diagnostics.
    /// </summary>
    public RelationalWriteTargetRequest TargetRequest { get; init; }

    /// <summary>
    /// The repository-resolved target document context for the active executor attempt.
    /// </summary>
    public RelationalWriteTargetContext TargetContext { get; init; }

    /// <summary>
    /// The compiled write plan selected for the write resource.
    /// </summary>
    public ResourceWritePlan WritePlan { get; init; }

    /// <summary>
    /// The compiled read plan selected for existing-document flows, when available.
    /// </summary>
    public ResourceReadPlan? ExistingDocumentReadPlan { get; init; }

    /// <summary>
    /// The caller-selected body the executor will eventually persist.
    /// </summary>
    public JsonNode SelectedBody { get; init; }

    /// <summary>
    /// Whether identity-changing writes are allowed for this resource once the executor supports them.
    /// </summary>
    public bool AllowIdentityUpdates { get; init; }

    /// <summary>
    /// The request trace id for diagnostics.
    /// </summary>
    public TraceId TraceId { get; init; }

    /// <summary>
    /// Reference-resolution inputs the executor must resolve inside the shared write session.
    /// </summary>
    public ReferenceResolverRequest ReferenceResolutionRequest { get; init; }

    /// <summary>
    /// Optional profile write context when a writable profile applies.
    /// Null when no profile applies. Downstream stages use this to decide whether to run the
    /// profile-constrained flatten/merge path or the no-profile merge/persist path.
    /// </summary>
    public BackendProfileWriteContext? ProfileWriteContext { get; init; }
}

/// <summary>
/// Backend-local classification of one executor attempt.
/// </summary>
public abstract record RelationalWriteExecutorAttemptOutcome
{
    /// <summary>
    /// The executor applied a real relational write.
    /// </summary>
    public sealed record AppliedWrite : RelationalWriteExecutorAttemptOutcome
    {
        private AppliedWrite() { }

        public static AppliedWrite Instance { get; } = new();
    }

    /// <summary>
    /// The executor proved the request was unchanged and committed a guarded no-op.
    /// </summary>
    public sealed record GuardedNoOp : RelationalWriteExecutorAttemptOutcome
    {
        private GuardedNoOp() { }

        public static GuardedNoOp Instance { get; } = new();
    }

    /// <summary>
    /// The executor detected an unchanged compare, but freshness was lost before success could be returned.
    /// </summary>
    public sealed record StaleNoOpCompare : RelationalWriteExecutorAttemptOutcome
    {
        private StaleNoOpCompare() { }

        public static StaleNoOpCompare Instance { get; } = new();
    }

    /// <summary>
    /// The executor exited through a non-attempt-success path such as validation or not-yet-implemented work.
    /// </summary>
    public sealed record Failed : RelationalWriteExecutorAttemptOutcome
    {
        private Failed() { }

        public static Failed Instance { get; } = new();
    }
}

/// <summary>
/// Executor result wrapper that preserves the repository's POST/PUT result split.
/// </summary>
public abstract record RelationalWriteExecutorResult
{
    protected RelationalWriteExecutorResult(RelationalWriteExecutorAttemptOutcome attemptOutcome)
    {
        AttemptOutcome = attemptOutcome ?? throw new ArgumentNullException(nameof(attemptOutcome));
    }

    /// <summary>
    /// Internal classification of the executor attempt.
    /// </summary>
    public RelationalWriteExecutorAttemptOutcome AttemptOutcome { get; init; }

    /// <summary>
    /// The executor completed a POST write path.
    /// </summary>
    public sealed record Upsert : RelationalWriteExecutorResult
    {
        public Upsert(UpsertResult result)
            : this(result, RelationalWriteExecutorAttemptOutcome.Failed.Instance) { }

        public Upsert(UpsertResult result, RelationalWriteExecutorAttemptOutcome attemptOutcome)
            : base(attemptOutcome)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        public UpsertResult Result { get; init; }

        public void Deconstruct(out UpsertResult result)
        {
            result = Result;
        }

        public void Deconstruct(
            out UpsertResult result,
            out RelationalWriteExecutorAttemptOutcome attemptOutcome
        )
        {
            result = Result;
            attemptOutcome = AttemptOutcome;
        }
    }

    /// <summary>
    /// The executor completed a PUT write path.
    /// </summary>
    public sealed record Update : RelationalWriteExecutorResult
    {
        public Update(UpdateResult result)
            : this(result, RelationalWriteExecutorAttemptOutcome.Failed.Instance) { }

        public Update(UpdateResult result, RelationalWriteExecutorAttemptOutcome attemptOutcome)
            : base(attemptOutcome)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        public UpdateResult Result { get; init; }

        public void Deconstruct(out UpdateResult result)
        {
            result = Result;
        }

        public void Deconstruct(
            out UpdateResult result,
            out RelationalWriteExecutorAttemptOutcome attemptOutcome
        )
        {
            result = Result;
            attemptOutcome = AttemptOutcome;
        }
    }
}

/// <summary>
/// Relational write executor invoked after repository guard rails and request shaping succeed.
/// </summary>
public interface IRelationalWriteExecutor
{
    /// <summary>
    /// Executes the relational write.
    /// </summary>
    Task<RelationalWriteExecutorResult> ExecuteAsync(
        RelationalWriteExecutorRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Shared validation and immutable-array helpers for flattened write contracts.
/// </summary>
internal static class FlattenedWriteContractSupport
{
    public static ImmutableArray<T> ToImmutableArray<T>(IEnumerable<T> items, string parameterName)
    {
        if (items is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        return [.. items];
    }

    public static void ValidateBindingCount(
        TableWritePlan tableWritePlan,
        ImmutableArray<FlattenedWriteValue> values,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);

        if (values.Length == tableWritePlan.ColumnBindings.Length)
        {
            return;
        }

        throw new ArgumentException(
            $"{parameterName} must contain one entry per {nameof(TableWritePlan.ColumnBindings)} value. "
                + $"Expected {tableWritePlan.ColumnBindings.Length}, actual {values.Length}.",
            parameterName
        );
    }

    public static void ValidateTableKind(
        TableWritePlan tableWritePlan,
        DbTableKind requiredKind,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);

        var actualKind = tableWritePlan.TableModel.IdentityMetadata.TableKind;

        if (actualKind == requiredKind)
        {
            return;
        }

        throw new ArgumentException(
            $"Expected table kind {requiredKind} but found {actualKind}.",
            parameterName
        );
    }
}

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
    public sealed record ExistingDocument(long DocumentId, DocumentUuid DocumentUuid)
        : RelationalWriteTargetContext;
}

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
        ResolvedReferenceSet resolvedReferences
    )
    {
        OperationKind = operationKind;
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        WritePlan = writePlan ?? throw new ArgumentNullException(nameof(writePlan));
        SelectedBody = selectedBody ?? throw new ArgumentNullException(nameof(selectedBody));
        ResolvedReferences =
            resolvedReferences ?? throw new ArgumentNullException(nameof(resolvedReferences));
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
    public sealed record UnresolvedCollectionItemId : FlattenedWriteValue
    {
        private UnresolvedCollectionItemId() { }

        public static UnresolvedCollectionItemId Instance { get; } = new();
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
        IEnumerable<CollectionWriteCandidate>? collectionCandidates = null
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
/// Input contract for the terminal write stage.
/// </summary>
public sealed record RelationalWriteTerminalStageRequest
{
    public RelationalWriteTerminalStageRequest(
        FlatteningInput flatteningInput,
        FlattenedWriteSet flattenedWriteSet,
        TraceId traceId,
        string? diagnosticIdentifier = null
    )
    {
        FlatteningInput = flatteningInput ?? throw new ArgumentNullException(nameof(flatteningInput));
        FlattenedWriteSet = flattenedWriteSet ?? throw new ArgumentNullException(nameof(flattenedWriteSet));
        TraceId = traceId;
        DiagnosticIdentifier = diagnosticIdentifier;
    }

    /// <summary>
    /// The selected write inputs used to produce <see cref="FlattenedWriteSet" />.
    /// </summary>
    public FlatteningInput FlatteningInput { get; init; }

    /// <summary>
    /// The flattened write tree produced from <see cref="FlatteningInput" />.
    /// </summary>
    public FlattenedWriteSet FlattenedWriteSet { get; init; }

    /// <summary>
    /// The request trace id for diagnostics.
    /// </summary>
    public TraceId TraceId { get; init; }

    /// <summary>
    /// Optional caller-supplied diagnostic identifier for logs or tracing.
    /// </summary>
    public string? DiagnosticIdentifier { get; init; }

    /// <summary>
    /// Optional profile write context for profile-constrained writes.
    /// Present when a writable profile applies and stored-state projection has completed.
    /// DMS-1124 consumes this for hidden-member preservation during merge execution.
    /// </summary>
    public ProfileAppliedWriteContext? ProfileWriteContext { get; init; }
}

/// <summary>
/// Terminal-stage result wrapper that preserves the repository's POST/PUT result split.
/// </summary>
public abstract record RelationalWriteTerminalStageResult
{
    /// <summary>
    /// The terminal stage completed a POST write path.
    /// </summary>
    public sealed record Upsert(UpsertResult Result) : RelationalWriteTerminalStageResult;

    /// <summary>
    /// The terminal stage completed a PUT write path.
    /// </summary>
    public sealed record Update(UpdateResult Result) : RelationalWriteTerminalStageResult;
}

/// <summary>
/// Final write stage invoked after plan selection, reference resolution, and flattening succeed.
/// </summary>
public interface IRelationalWriteTerminalStage
{
    /// <summary>
    /// Executes the terminal relational write stage.
    /// </summary>
    Task<RelationalWriteTerminalStageResult> ExecuteAsync(
        RelationalWriteTerminalStageRequest request,
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

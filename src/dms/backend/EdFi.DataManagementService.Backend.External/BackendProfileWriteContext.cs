// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.External;

public sealed record ResolvedProfileWriteResult(
    ProfileAppliedWriteRequest? Request,
    ProfileAppliedWriteContext? Context,
    ImmutableArray<ProfileFailure> Failures
)
{
    public static ResolvedProfileWriteResult Success(
        ProfileAppliedWriteRequest request,
        ProfileAppliedWriteContext? context = null
    ) => new(request, context, []);

    public static ResolvedProfileWriteResult Failure(IEnumerable<ProfileFailure> failures) =>
        new(null, null, [.. failures]);
}

/// <summary>
/// Callback interface for deferred resolved-target profile execution. Middleware provides
/// an implementation that captures the pre-resolution request, writable content type,
/// and effective required-member map; the backend invokes it after target resolution.
/// </summary>
public interface IResolvedProfileWriteInvoker
{
    /// <summary>
    /// Executes the resolved-target phase of profile write processing using the
    /// stored document and the final create-vs-update classification.
    /// </summary>
    /// <param name="storedDocument">The current stored JSON document loaded by the repository.</param>
    /// <param name="isCreate">Whether the resolved target is a new create.</param>
    /// <param name="scopeCatalog">The compiled scope descriptors for the target resource.</param>
    ResolvedProfileWriteResult Execute(
        JsonNode? storedDocument,
        bool isCreate,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    );
}

/// <summary>
/// Legacy callback interface retained for hand-built test contexts that only
/// project stored state for update flows.
/// </summary>
public interface IStoredStateProjectionInvoker
{
    ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    );
}

/// <summary>
/// Composite type carrying all profile data needed by the backend write path.
/// Produced by <c>ProfileWritePipelineMiddleware</c> and threaded through request records
/// to the repository orchestration layer.
/// </summary>
public sealed record BackendProfileWriteContext(
    ProfilePreResolvedWriteRequest PreResolvedRequest,
    string ProfileName,
    IReadOnlyList<CompiledScopeDescriptor> CompiledScopeCatalog,
    IResolvedProfileWriteInvoker ResolvedProfileWriteInvoker
)
{
    public ProfileAppliedWriteRequest? Request { get; init; }

    public BackendProfileWriteContext(
        ProfileAppliedWriteRequest Request,
        string ProfileName,
        IReadOnlyList<CompiledScopeDescriptor> CompiledScopeCatalog,
        IStoredStateProjectionInvoker StoredStateProjectionInvoker
    )
        : this(
            new ProfilePreResolvedWriteRequest(
                Request.WritableRequestBody,
                Request.RequestScopeStates,
                Request.VisibleRequestCollectionItems
            ),
            ProfileName,
            CompiledScopeCatalog,
            new LegacyStoredStateProjectionResolvedProfileWriteInvoker(Request, StoredStateProjectionInvoker)
        )
    {
        this.Request = Request;
    }
}

file sealed class LegacyStoredStateProjectionResolvedProfileWriteInvoker(
    ProfileAppliedWriteRequest request,
    IStoredStateProjectionInvoker storedStateProjectionInvoker
) : IResolvedProfileWriteInvoker
{
    public ResolvedProfileWriteResult Execute(
        JsonNode? storedDocument,
        bool isCreate,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        if (isCreate || storedDocument is null)
        {
            return ResolvedProfileWriteResult.Success(request);
        }

        var context = storedStateProjectionInvoker.ProjectStoredState(storedDocument, request, scopeCatalog);

        return ResolvedProfileWriteResult.Success(request, context);
    }
}

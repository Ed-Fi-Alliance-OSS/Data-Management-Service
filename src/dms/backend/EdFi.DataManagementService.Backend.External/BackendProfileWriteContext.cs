// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Callback interface for deferred stored-state projection. Middleware provides
/// an implementation that captures C6 projection dependencies; the repository
/// calls it when the stored document is available during update/upsert-to-existing flows.
/// </summary>
public interface IStoredStateProjectionInvoker
{
    /// <summary>
    /// Projects the stored document into a <see cref="ProfileAppliedWriteContext"/>
    /// using the captured profile pipeline state.
    /// </summary>
    /// <param name="storedDocument">The current stored JSON document loaded by the repository.</param>
    /// <param name="request">The request-side profile contract.</param>
    /// <param name="scopeCatalog">The compiled scope descriptors for the target resource.</param>
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
/// <param name="Request">The request-side profile contract from the profile write pipeline.</param>
/// <param name="ProfileName">The name of the profile governing this write request.</param>
/// <param name="CompiledScopeCatalog">The compiled scope descriptors for the target resource.</param>
/// <param name="StoredStateProjectionInvoker">
/// Callback to invoke C6 stored-state projection when the stored document is available.
/// </param>
public sealed record BackendProfileWriteContext(
    ProfileAppliedWriteRequest Request,
    string ProfileName,
    IReadOnlyList<CompiledScopeDescriptor> CompiledScopeCatalog,
    IStoredStateProjectionInvoker StoredStateProjectionInvoker
);

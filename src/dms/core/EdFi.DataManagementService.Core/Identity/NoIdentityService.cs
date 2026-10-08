// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Identity;

namespace EdFi.DataManagementService.Core.Identity;

/// <summary>
/// The host default <see cref="IIdentityService"/> registered when no identity provider plugin
/// supplies one.
/// <para>
/// <see cref="Capabilities"/> returns <see cref="IdentityCapabilities.None"/>, and the host gates
/// every identity route on that captured value before invoking an operation - so reaching one of the
/// methods below means the host failed to honor its own gate, not that a provider produced an
/// outcome. That is why these methods throw <see cref="NotSupportedException"/> rather than returning
/// an <c>IdentityResultStatus</c>: a status return here would misrepresent a host defect as a normal
/// provider result.
/// </para>
/// </summary>
internal sealed class NoIdentityService : IIdentityService
{
    internal const string ConfigurationErrorMessage =
        "No identity provider plugin is registered, so identity management is unavailable. Load an "
        + "identity provider plugin that registers an IIdentityService implementation to enable it.";

    public IdentityCapabilities Capabilities => IdentityCapabilities.None;

    public Task<IdentityResult> CreateAsync(
        JsonObject request,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException(ConfigurationErrorMessage);

    public Task<IdentityResult> GetByIdAsync(
        string uniqueId,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException(ConfigurationErrorMessage);

    public Task<IdentityAsyncResult> FindAsync(
        IReadOnlyList<string> uniqueIds,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException(ConfigurationErrorMessage);

    public Task<IdentityAsyncResult> SearchAsync(
        IReadOnlyList<JsonObject> requests,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException(ConfigurationErrorMessage);

    public Task<IdentityResult> ResultsAsync(
        string requestToken,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException(ConfigurationErrorMessage);
}

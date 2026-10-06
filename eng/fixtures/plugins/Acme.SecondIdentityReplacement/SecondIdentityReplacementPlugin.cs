// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.Api.Plugins;
using EdFi.DataManagementService.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.SecondIdentityReplacement;

public sealed class SecondIdentityReplacementPlugin : EdFiApiPlugin
{
    // Must equal the name of the directory this plugin is published into and named by in
    // Plugins:Allowed. The host treats a mismatch as fatal.
    public override string Name => "Acme.SecondIdentityReplacement";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // One plain Add. Loaded alongside another identity plugin, the host's two-claim check fails
        // startup before any service is activated, so this implementation is never constructed.
        services.AddScoped<IIdentityService, UnusedIdentityService>();
    }

    private sealed class UnusedIdentityService : IIdentityService
    {
        public IdentityCapabilities Capabilities => IdentityCapabilities.None;

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityResult> GetByIdAsync(
            string uniqueId,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityAsyncResult> FindAsync(
            IReadOnlyList<string> uniqueIds,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityAsyncResult> SearchAsync(
            IReadOnlyList<JsonObject> requests,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityResult> ResultsAsync(
            string requestToken,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }
}

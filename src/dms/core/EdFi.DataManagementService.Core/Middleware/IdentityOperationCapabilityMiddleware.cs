// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Identity;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Resolves the registered <see cref="IIdentityService" /> once per request through
/// <see cref="IdentityProviderBoundary" />, reads its <c>Capabilities</c> exactly once, and gates the
/// requested route's <see cref="Identity.IdentityOperation" /> against the captured value
/// (design.md "Pipeline", D9). An unsupported operation, including on a request whose POST body is
/// unparseable or carries a duplicate property, returns operation-unsupported <c>404</c> before any
/// content-type or body validation runs - so "enabled with no plugin" answers not-implemented
/// semantics for every request shape, not only well-formed ones.
/// <para>
/// The resolved instance and the captured capabilities are stored on
/// <see cref="RequestInfo.IdentityProvider" /> and <see cref="RequestInfo.IdentityCapabilities" /> so
/// <see cref="Handler.IdentityHandler" /> invokes the same instance the gate observed and never rereads
/// the getter, keeping the results-token capability invariant and the operation gate in agreement.
/// </para>
/// <para>
/// Activation or capability-getter failure already produced a sanitized provider-configuration
/// response through the boundary; this middleware runs no further checks and calls no operation in
/// that case.
/// </para>
/// </summary>
internal sealed class IdentityOperationCapabilityMiddleware(
    IdentityProviderBoundary _boundary,
    ILogger<IdentityOperationCapabilityMiddleware> _logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering IdentityOperationCapabilityMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        IIdentityService? provider = _boundary.Activate(requestInfo);
        if (provider is null)
        {
            // Activation failed: the boundary already set a sanitized provider-configuration 500.
            return;
        }

        IdentityCapabilities? capabilities = _boundary.ReadCapabilities(provider, requestInfo);
        if (capabilities is null)
        {
            // The capability getter failed: the boundary already set a sanitized provider-configuration 500.
            return;
        }

        requestInfo.IdentityProvider = provider;
        requestInfo.IdentityCapabilities = capabilities.Value;

        if (!RequestedOperationIsSupported(capabilities.Value, requestInfo.IdentityOperation))
        {
            _logger.LogDebug(
                "Identity operation {Operation} is not supported by the registered provider's Capabilities {Capabilities} - {TraceId}",
                requestInfo.IdentityOperation,
                capabilities.Value,
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 404,
                Body: IdentityFailureResponse.ForIdentityOperationNotSupported(
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );
            return;
        }

        await next();
    }

    private static bool RequestedOperationIsSupported(
        IdentityCapabilities capabilities,
        IdentityOperation? operation
    ) =>
        operation switch
        {
            IdentityOperation.Create => capabilities.HasFlag(IdentityCapabilities.Create),
            IdentityOperation.GetById => capabilities.HasFlag(IdentityCapabilities.GetById),
            IdentityOperation.Find => capabilities.HasFlag(IdentityCapabilities.Find),
            IdentityOperation.Search => capabilities.HasFlag(IdentityCapabilities.Search),
            IdentityOperation.Results => capabilities.HasFlag(IdentityCapabilities.Results),
            _ => false,
        };
}

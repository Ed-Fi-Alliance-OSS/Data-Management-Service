// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Action policy pairs for tests whose POST authorization is intentionally the same for Create and Update.
/// </summary>
public static class UpsertActionAuthorizationTestSupport
{
    /// <summary>
    /// Both actions permitted with <c>NoFurtherAuthorizationRequired</c> only: the write is authorized
    /// without any record-level check, whichever branch the POST takes.
    /// </summary>
    public static UpsertActionAuthorization NoFurtherAuthorizationRequiredForCreateAndUpdate { get; } =
        UpsertActionAuthorization.SamePolicyForCreateAndUpdate([
            new AuthorizationStrategyEvaluator(
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                [],
                FilterOperator.Or
            ),
        ]);

    /// <summary>
    /// Runs a descriptor POST whose Create and Update policies are both the evaluators the test placed on
    /// the request. A request built with no evaluators stands for <c>NoFurtherAuthorizationRequired</c>,
    /// the no-check policy a production POST would carry; production never sends an empty policy.
    /// </summary>
    public static Task<UpsertResult> HandlePostWithSamePolicyForCreateAndUpdateAsync(
        this IDescriptorWriteHandler handler,
        DescriptorWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(request);

        var actionAuthorization =
            request.AuthorizationStrategyEvaluators.Length == 0
                ? NoFurtherAuthorizationRequiredForCreateAndUpdate
                : UpsertActionAuthorization.SamePolicyForCreateAndUpdate(
                    request.AuthorizationStrategyEvaluators
                );

        return handler.HandlePostAsync(
            request with
            {
                AuthorizationStrategyEvaluators = [],
            },
            actionAuthorization,
            cancellationToken
        );
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class PostActionPolicySupport
{
    /// <summary>
    /// Returns the evaluators a POST authorizes with when its Create and Update policies are the same.
    /// Selecting between differing policies by target is not supported, so a request whose policies differ
    /// fails closed before any write session opens.
    /// </summary>
    public static AuthorizationStrategyEvaluator[] RequireSharedPolicy(
        UpsertActionAuthorization actionAuthorization
    )
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);

        return actionAuthorization.TryGetSharedPolicy(out var evaluators)
            ? evaluators
            : throw new NotSupportedException(
                "A POST whose Create and Update authorization policies differ is not supported."
            );
    }
}

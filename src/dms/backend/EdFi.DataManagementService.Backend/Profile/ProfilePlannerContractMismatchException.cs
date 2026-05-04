// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Thrown by profile-aware planners (e.g. <see cref="ProfileTopLevelCollectionPlanner"/>) when
/// Core-emitted profile/scope metadata fails a fail-closed planner invariant. Each such failure
/// represents a Core/backend contract mismatch — Core handed the backend a profile/schema
/// combination that the compiled scope catalog cannot satisfy — not a backend internal bug.
///
/// <para>The executor (<see cref="DefaultRelationalWriteExecutor"/>) catches this exception
/// type narrowly and shapes it as a profile contract-mismatch result, mirroring the result
/// shape produced by <see cref="ProfileWriteContractValidator"/>. Generic
/// <see cref="InvalidOperationException"/>s remain fail-fast for true internal bugs.</para>
///
/// <para>The <see cref="JsonScope"/> and <see cref="InvariantName"/> are diagnostic surface for
/// the executor's failure-message shaping; they intentionally mirror the previous exception
/// message contents so existing test assertions on those substrings continue to pass.</para>
/// </summary>
public sealed class ProfilePlannerContractMismatchException(
    string jsonScope,
    string invariantName,
    string message
) : Exception(message)
{
    public string JsonScope { get; } = jsonScope ?? throw new ArgumentNullException(nameof(jsonScope));

    public string InvariantName { get; } =
        invariantName ?? throw new ArgumentNullException(nameof(invariantName));
}

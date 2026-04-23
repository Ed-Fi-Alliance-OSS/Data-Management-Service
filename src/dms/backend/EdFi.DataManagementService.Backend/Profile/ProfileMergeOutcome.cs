// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Outcome of <see cref="IRelationalWriteProfileMergeSynthesizer.Synthesize"/>.
/// Exactly one of <see cref="MergeResult"/> or <see cref="CreatabilityRejection"/>
/// is non-null. Use <see cref="IsRejection"/> to discriminate.
/// </summary>
internal readonly record struct ProfileMergeOutcome
{
    public RelationalWriteMergeResult? MergeResult { get; init; }
    public ProfileCreatabilityRejection? CreatabilityRejection { get; init; }

    public bool IsRejection => CreatabilityRejection is not null;

    public static ProfileMergeOutcome Success(RelationalWriteMergeResult result) =>
        new() { MergeResult = result ?? throw new ArgumentNullException(nameof(result)) };

    public static ProfileMergeOutcome Reject(ProfileCreatabilityRejection rejection) =>
        new() { CreatabilityRejection = rejection ?? throw new ArgumentNullException(nameof(rejection)) };
}

/// <summary>
/// Carries the context needed to map a profiled create-denied outcome
/// to the executor's typed profile-data-policy failure response.
/// </summary>
/// <param name="ScopeJsonScope">The JSON scope where creation was denied (root $ or a separate-table scope like $._ext.sample).</param>
/// <param name="Message">Human-readable diagnostic message (internal; not shown to clients).</param>
internal sealed record ProfileCreatabilityRejection(string ScopeJsonScope, string Message);

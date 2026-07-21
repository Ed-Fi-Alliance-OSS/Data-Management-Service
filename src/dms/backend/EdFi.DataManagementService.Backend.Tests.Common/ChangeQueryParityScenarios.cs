// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Provider-agnostic assertion helpers for change-query read-back parity: the served
/// <c>changeVersion</c> must come from the stored tracked-change stamp, not be recomputed or merely
/// internally consistent. Each provider suite keeps its own provisioning, writes, tracked-change
/// stamp readback, and query execution; it passes the served result and the persisted stamp values
/// and delegates the behavioral assertion here.
/// </summary>
public static class ChangeQueryParityScenarios
{
    /// <summary>
    /// Asserts a key-changes query collapsed a multi-step AcademicWeek key-change chain to exactly
    /// one served item whose <c>changeVersion</c> equals the LATEST persisted tracked-change
    /// ChangeVersion stamp (with the chain proven non-vacuous: the latest stamp strictly advanced
    /// past the first), whose id is the document uuid, and whose collapsed old/new key values span
    /// the full chain (original old key, final new key, unchanged school id).
    /// </summary>
    public static void AssertAcademicWeekKeyChangesCollapsedToLatestStoredChangeVersion(
        TrackedChangeQueryResult result,
        Guid documentUuid,
        long firstPersistedChangeVersion,
        long latestPersistedChangeVersion,
        long schoolId,
        string oldWeekIdentifier,
        string newWeekIdentifier
    )
    {
        // Non-vacuous chain: the persisted stamps must have advanced across the key changes, so
        // "served equals latest" cannot pass because first and latest are accidentally equal.
        latestPersistedChangeVersion
            .Should()
            .BeGreaterThan(
                firstPersistedChangeVersion,
                "the key-change chain must persist strictly advancing tracked-change stamps"
            );

        result.TotalCount.Should().Be(1L);
        result.Items.Should().ContainSingle();

        JsonObject item = result.Items[0]!.AsObject();
        item["id"]!.GetValue<string>().Should().Be(documentUuid.ToString("D"));
        item["changeVersion"]!
            .GetValue<long>()
            .Should()
            .Be(
                latestPersistedChangeVersion,
                "the served changeVersion is the latest stored tracked-change stamp"
            );

        JsonObject oldKeyValues = item["oldKeyValues"]!.AsObject();
        oldKeyValues["weekIdentifier"]!.GetValue<string>().Should().Be(oldWeekIdentifier);
        oldKeyValues["schoolId"]!.GetValue<long>().Should().Be(schoolId);

        JsonObject newKeyValues = item["newKeyValues"]!.AsObject();
        newKeyValues["weekIdentifier"]!.GetValue<string>().Should().Be(newWeekIdentifier);
        newKeyValues["schoolId"]!.GetValue<long>().Should().Be(schoolId);
    }
}

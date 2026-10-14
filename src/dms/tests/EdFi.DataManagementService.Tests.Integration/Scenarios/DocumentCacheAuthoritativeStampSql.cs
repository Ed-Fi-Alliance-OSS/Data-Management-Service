// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class DocumentCacheAuthoritativeStampSql
{
    // These scenarios use only the descriptor-runtime and profile-root-only fixtures.
    // Fail on another fixture rather than silently dropping an unrecognized owner.
    public static string Owners(ApiIntegrationHarness harness) =>
        harness.Fixture.Key switch
        {
            FixtureKey.DescriptorRuntime => """
                SELECT "DocumentId", "ContentLastModifiedAt" FROM "dms"."Descriptor"
                """,
            FixtureKey.ProfileRootOnlyMerge => """
                SELECT "DocumentId", "ContentLastModifiedAt" FROM "dms"."Descriptor"
                UNION ALL SELECT "DocumentId", "ContentLastModifiedAt" FROM "edfi"."Student"
                UNION ALL SELECT "DocumentId", "ContentLastModifiedAt" FROM "edfi"."ProfileRootOnlyMergeItem"
                """,
            _ => throw new InvalidOperationException(
                $"Unsupported cache scenario fixture: {harness.Fixture.Key}"
            ),
        };
}

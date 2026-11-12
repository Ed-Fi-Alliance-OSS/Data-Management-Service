// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

/// <summary>
/// How the DMS provisioning integration lanes treat a missing lane setting (Step 5.2). Locally the fixture is
/// skipped. Under GitHub Actions the <c>run-dms-provisioning-integration-tests</c> job exports every setting, so a
/// missing one is a broken lane and the fixture fails instead of passing as a skip. Only that job runs the
/// <c>DmsProvisioning</c> category; every other CI test run excludes it.
/// </summary>
public static class DmsProvisioningLaneRequirement
{
    public static bool IsGitHubActions =>
        string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>Skips the fixture locally; fails it under GitHub Actions. Never returns.</summary>
    [DoesNotReturn]
    public static void SkipLocallyOrFailInCi(string reason)
    {
        if (IsGitHubActions)
        {
            Assert.Fail($"{reason} The DMS provisioning lane must export it under GitHub Actions.");
        }

        Assert.Ignore(reason);
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration;

public class ClaimsHierarchyTests : DatabaseTest
{
    private readonly IClaimsHierarchyRepository _repository = new ClaimsHierarchyRepository(
        Configuration.DatabaseOptions,
        NullLogger<ClaimsHierarchyRepository>.Instance
    );

    [Test]
    public async Task Should_get_claims_hierarchy()
    {
        var claimsHierarchyResult = await _repository.GetClaimsHierarchy();
        claimsHierarchyResult.Should().BeOfType<ClaimsHierarchyGetResult.Success>();

        var hierarchy = ((ClaimsHierarchyGetResult.Success)claimsHierarchyResult).Claims;
        hierarchy.Should().NotBeNull();
        hierarchy.Should().BeOfType<List<Claim>>();

        hierarchy.Count.Should().BeGreaterThan(0);

        // Verify parent-child relationships for each root claim
        foreach (var rootClaim in hierarchy)
        {
            // Root claims should have no parent
            rootClaim.Parent.Should().BeNull();

            // Verify the hierarchy starting from this root claim
            VerifyClaimsHierarchyNavigability(rootClaim);
        }

        // Helper method to verify parent-child relationships in a claim hierarchy
        void VerifyClaimsHierarchyNavigability(Claim claim)
        {
            // Verify each child claim's parent reference
            if (claim.Claims.Any())
            {
                foreach (var child in claim.Claims)
                {
                    // Child's parent should reference the current claim
                    child.Parent.Should().Be(claim);

                    // Recursively verify the child's hierarchy
                    VerifyClaimsHierarchyNavigability(child);
                }
            }
        }
    }
}

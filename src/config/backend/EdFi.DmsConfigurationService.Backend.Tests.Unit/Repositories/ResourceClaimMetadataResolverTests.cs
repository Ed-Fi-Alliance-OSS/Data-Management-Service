// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Repositories;

[TestFixture]
public class ResourceClaimMetadataResolverTests
{
    [Test]
    public void It_resolves_a_nested_hierarchy_claim_by_resource_claim_id()
    {
        IReadOnlyList<Claim> claims = [new() { Name = "claim-a", Claims = [new() { Name = "claim-b" }] }];
        IReadOnlyList<ResourceClaimMetadataRow> metadata =
        [
            new(1, "Resource A", "claim-a"),
            new(2, "Resource B", "claim-b"),
        ];

        var result = ResourceClaimMetadataResolver.Resolve(2, claims, metadata);

        result
            .Should()
            .BeOfType<ResourceClaimMetadataResolveResult.Success>()
            .Which.ClaimName.Should()
            .Be("claim-b");
    }

    [Test]
    public void It_returns_projection_integrity_failure_when_a_hierarchy_claim_has_no_metadata()
    {
        IReadOnlyList<Claim> claims = [new() { Name = "missing-claim-uri" }];
        IReadOnlyList<ResourceClaimMetadataRow> metadata = [];

        var result = ResourceClaimMetadataResolver.Resolve(2, claims, metadata);

        result
            .Should()
            .BeOfType<ResourceClaimMetadataResolveResult.FailureProjectionIntegrity>()
            .Which.FailureMessage.Should()
            .Contain("missing-claim-uri");
    }
}

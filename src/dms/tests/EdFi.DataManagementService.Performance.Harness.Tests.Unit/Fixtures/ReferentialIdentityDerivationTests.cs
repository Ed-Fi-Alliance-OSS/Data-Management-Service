// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Uuidv5_Implementation
{
    // The RFC 4122 DNS namespace and the well-known python.org example vector.
    private static readonly Guid _dnsNamespace = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    [Test]
    public void It_reproduces_the_published_reference_vector()
    {
        ReferentialIdentityDerivation
            .Uuidv5(_dnsNamespace, "python.org")
            .Should()
            .Be(Guid.Parse("886313e1-3b8a-5372-9b90-0c9aee199e5d"));
    }

    [Test]
    public void It_stamps_the_version_and_variant_bits()
    {
        string text = ReferentialIdentityDerivation
            .Uuidv5(ReferentialIdentityDerivation.EdFiNamespace, "anything")
            .ToString("D");
        text[14].Should().Be('5');
        "89ab".Should().Contain(text[19].ToString());
    }
}

[TestFixture]
public class Given_The_Student_Referential_Id_Derivation
{
    [Test]
    public void It_hashes_the_project_resource_and_key_path()
    {
        ReferentialIdentityDerivation
            .StudentReferentialId("perf-000000001")
            .Should()
            .Be(
                ReferentialIdentityDerivation.Uuidv5(
                    ReferentialIdentityDerivation.EdFiNamespace,
                    "Ed-FiStudent$.studentUniqueId=perf-000000001"
                )
            );
    }

    [Test]
    public void It_is_deterministic()
    {
        ReferentialIdentityDerivation
            .StudentReferentialId("perf-000000042")
            .Should()
            .Be(ReferentialIdentityDerivation.StudentReferentialId("perf-000000042"));
    }

    [Test]
    public void It_is_unique_across_unique_ids()
    {
        Enumerable
            .Range(1, 500)
            .Select(ordinal =>
                ReferentialIdentityDerivation.StudentReferentialId(
                    PerfFixtureDefinition.StudentUniqueIdFor(ordinal)
                )
            )
            .Should()
            .OnlyHaveUniqueItems();
    }
}

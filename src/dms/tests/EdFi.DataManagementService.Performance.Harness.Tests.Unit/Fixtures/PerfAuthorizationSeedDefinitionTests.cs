// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Authorization_Seed_Definition
{
    [Test]
    public void It_enrolls_every_second_student_of_the_primary_fixture()
    {
        new PerfAuthorizationSeedDefinition(new PerfFixtureDefinition(PerfFixtureKind.Primary500k))
            .EnrolledStudentCount.Should()
            .Be(250_000);
        new PerfAuthorizationSeedDefinition(new PerfFixtureDefinition(PerfFixtureKind.Smoke10k))
            .EnrolledStudentCount.Should()
            .Be(5_000);
    }

    [Test]
    public void It_allocates_seed_documents_directly_above_the_primary_reseed_target()
    {
        PerfAuthorizationSeedDefinition seed = new(new PerfFixtureDefinition(PerfFixtureKind.Primary500k));

        seed.SchoolDocumentId.Should().Be(555_562);
        seed.GradeLevelDescriptorDocumentId.Should().Be(555_563);
        seed.SsaDocumentIdBase.Should().Be(555_563, "the first association occupies base + 1");
        seed.SsaMaxDocumentId.Should().Be(805_563);
        seed.ReseedTargetDocumentId.Should().Be(805_563);
    }

    [Test]
    public void It_allocates_the_smoke_seed_documents_consistently()
    {
        PerfAuthorizationSeedDefinition seed = new(new PerfFixtureDefinition(PerfFixtureKind.Smoke10k));

        seed.SchoolDocumentId.Should().Be(11_118);
        seed.GradeLevelDescriptorDocumentId.Should().Be(11_119);
        seed.SsaMaxDocumentId.Should().Be(16_119);
    }

    [Test]
    public void It_enrolls_even_row_ordinals()
    {
        PerfAuthorizationSeedDefinition.EnrolledStudentOrdinal(1).Should().Be(2);
        PerfAuthorizationSeedDefinition.EnrolledStudentOrdinal(2).Should().Be(4);
        PerfAuthorizationSeedDefinition.EnrolledStudentOrdinal(2_500).Should().Be(5_000);
    }

    [Test]
    public void It_computes_the_enrolled_document_id_checksum()
    {
        // Ordinals 2,4,...,20 map to DocumentIds 3,5,7,9,12,14,16,18,20,23.
        PerfAuthorizationSeedDefinition seed = new(
            new PerfFixtureDefinition(new PerfFixtureKind("test-20", 20))
        );

        seed.EnrolledStudentDocumentIdSum().Should().Be(127);
    }

    [Test]
    public void It_derives_association_document_uuids_from_the_candidate_index()
    {
        PerfAuthorizationSeedDefinition
            .SsaDocumentUuidFor(1)
            .Should()
            .Be(Guid.Parse("8f7a1000-0000-4000-8000-000000000001"));
        PerfAuthorizationSeedDefinition
            .SsaDocumentUuidFor(255)
            .Should()
            .Be(Guid.Parse("8f7a1000-0000-4000-8000-0000000000ff"));
    }

    [Test]
    public void It_keeps_the_seed_uuid_prefixes_disjoint_from_the_student_prefix()
    {
        PerfAuthorizationSeedDefinition
            .SsaDocumentUuidPrefix.Should()
            .NotBe(PerfFixtureDefinition.DocumentUuidPrefix);
        PerfAuthorizationSeedDefinition
            .SchoolDocumentUuid.ToString()
            .Should()
            .NotStartWith(PerfFixtureDefinition.DocumentUuidPrefix);
    }

    [Test]
    public void It_names_the_grade_level_descriptor_like_the_fixture_catalog()
    {
        PerfAuthorizationSeedDefinition
            .GradeLevelDescriptorUri.Should()
            .Be("uri://ed-fi.org/GradeLevelDescriptor#Perf");
    }
}

[TestFixture]
public class Given_The_Association_Referential_Identity_Derivation
{
    [Test]
    public void It_is_deterministic()
    {
        Guid first = ReferentialIdentityDerivation.StudentSchoolAssociationReferentialId(
            "2025-08-11",
            8_990_001,
            "perf-000000002"
        );
        Guid second = ReferentialIdentityDerivation.StudentSchoolAssociationReferentialId(
            "2025-08-11",
            8_990_001,
            "perf-000000002"
        );

        first.Should().Be(second);
    }

    [Test]
    public void It_changes_when_any_identity_component_changes()
    {
        Guid baseline = ReferentialIdentityDerivation.StudentSchoolAssociationReferentialId(
            "2025-08-11",
            8_990_001,
            "perf-000000002"
        );

        ReferentialIdentityDerivation
            .StudentSchoolAssociationReferentialId("2025-08-12", 8_990_001, "perf-000000002")
            .Should()
            .NotBe(baseline);
        ReferentialIdentityDerivation
            .StudentSchoolAssociationReferentialId("2025-08-11", 8_990_002, "perf-000000002")
            .Should()
            .NotBe(baseline);
        ReferentialIdentityDerivation
            .StudentSchoolAssociationReferentialId("2025-08-11", 8_990_001, "perf-000000004")
            .Should()
            .NotBe(baseline);
    }

    [Test]
    public void It_stamps_the_version_5_and_variant_bits()
    {
        string text = ReferentialIdentityDerivation
            .StudentSchoolAssociationReferentialId("2025-08-11", 8_990_001, "perf-000000002")
            .ToString("D");

        text[14].Should().Be('5');
        "89ab".Should().Contain(text[19].ToString());
    }
}

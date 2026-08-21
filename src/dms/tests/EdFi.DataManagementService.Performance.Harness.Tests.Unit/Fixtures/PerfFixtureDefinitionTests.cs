// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Primary_Fixture_Definition
{
    private PerfFixtureDefinition _definition = null!;

    [SetUp]
    public void Setup()
    {
        _definition = new PerfFixtureDefinition(PerfFixtureKind.Primary500k);
    }

    [Test]
    public void It_carries_the_epic_row_count()
    {
        _definition.RowCount.Should().Be(500_000);
    }

    [Test]
    public void It_starts_after_the_leading_gap()
    {
        PerfFixtureDefinition.MinDocumentId.Should().Be(2);
    }

    [Test]
    public void It_ends_at_the_expected_maximum_id()
    {
        _definition.MaxDocumentId.Should().Be(555_556);
    }

    [Test]
    public void It_counts_the_gaps_analytically()
    {
        _definition.GapCount.Should().Be(55_556);
    }

    [Test]
    public void It_meets_the_ten_percent_gap_density_floor()
    {
        _definition.GapDensity.Should().BeGreaterThanOrEqualTo(0.10);
    }

    [Test]
    public void It_does_not_overshoot_the_intended_density()
    {
        _definition.GapDensity.Should().BeLessThan(0.101);
    }

    [Test]
    public void It_sums_document_ids_to_the_closed_form()
    {
        _definition.DocumentIdSum().Should().Be(ClosedFormDocumentIdSum(_definition.RowCount));
    }

    [Test]
    public void It_pins_the_resource_identity_constants()
    {
        PerfFixtureDefinition.ProjectName.Should().Be("Ed-Fi");
        PerfFixtureDefinition.ResourceName.Should().Be("Student");
        PerfFixtureDefinition.ResourceEndpoint.Should().Be("/data/ed-fi/students");
    }

    // Independent of DocumentIdFor: each complete block b of nine rows holds ids
    // 10b+2..10b+10 summing to 90b+54; a partial final block of r rows starts at 10F+2.
    internal static long ClosedFormDocumentIdSum(long rowCount)
    {
        long fullBlocks = rowCount / 9;
        long remainder = rowCount % 9;
        long fullBlockSum = (90 * fullBlocks * (fullBlocks - 1) / 2) + (54 * fullBlocks);
        long remainderSum = (remainder * ((10 * fullBlocks) + 2)) + (remainder * (remainder - 1) / 2);
        return fullBlockSum + remainderSum;
    }
}

[TestFixture]
public class Given_The_Smoke_Fixture_Definition
{
    private PerfFixtureDefinition _definition = null!;

    [SetUp]
    public void Setup()
    {
        _definition = new PerfFixtureDefinition(PerfFixtureKind.Smoke10k);
    }

    [Test]
    public void It_carries_the_smoke_row_count()
    {
        _definition.RowCount.Should().Be(10_000);
    }

    [Test]
    public void It_ends_at_the_expected_maximum_id()
    {
        _definition.MaxDocumentId.Should().Be(11_112);
    }

    [Test]
    public void It_counts_the_gaps_analytically()
    {
        _definition.GapCount.Should().Be(1_112);
    }

    [Test]
    public void It_meets_the_ten_percent_gap_density_floor()
    {
        _definition.GapDensity.Should().BeGreaterThanOrEqualTo(0.10);
    }

    [Test]
    public void It_sums_document_ids_to_the_closed_form()
    {
        _definition
            .DocumentIdSum()
            .Should()
            .Be(Given_The_Primary_Fixture_Definition.ClosedFormDocumentIdSum(_definition.RowCount));
    }
}

[TestFixture]
public class Given_The_Document_Id_Mapping
{
    [Test]
    public void It_maps_the_first_block_around_the_leading_gap()
    {
        PerfFixtureDefinition.DocumentIdFor(1).Should().Be(2);
        PerfFixtureDefinition.DocumentIdFor(9).Should().Be(10);
        PerfFixtureDefinition.DocumentIdFor(10).Should().Be(12);
        PerfFixtureDefinition.DocumentIdFor(18).Should().Be(20);
        PerfFixtureDefinition.DocumentIdFor(19).Should().Be(22);
    }

    [Test]
    public void It_is_strictly_increasing_and_skips_every_gap_id_over_the_full_primary_range()
    {
        List<long> violatingOrdinals = [];
        long previous = 0;
        for (long ordinal = 1; ordinal <= PerfFixtureKind.Primary500k.RowCount; ordinal++)
        {
            long documentId = PerfFixtureDefinition.DocumentIdFor(ordinal);
            if (documentId <= previous || documentId % 10 == 1)
            {
                violatingOrdinals.Add(ordinal);
            }

            previous = documentId;
        }

        violatingOrdinals.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_non_positive_ordinals()
    {
        FluentActions
            .Invoking(() => PerfFixtureDefinition.DocumentIdFor(0))
            .Should()
            .Throw<ArgumentOutOfRangeException>();
        FluentActions
            .Invoking(() => PerfFixtureDefinition.DocumentIdFor(-5))
            .Should()
            .Throw<ArgumentOutOfRangeException>();
    }
}

[TestFixture]
public class Given_The_Student_Unique_Id_Derivation
{
    [Test]
    public void It_pads_to_nine_digits()
    {
        PerfFixtureDefinition.StudentUniqueIdFor(1).Should().Be("perf-000000001");
        PerfFixtureDefinition.StudentUniqueIdFor(500_000).Should().Be("perf-000500000");
    }

    [Test]
    public void It_is_deterministic()
    {
        PerfFixtureDefinition
            .StudentUniqueIdFor(12_345)
            .Should()
            .Be(PerfFixtureDefinition.StudentUniqueIdFor(12_345));
    }

    [Test]
    public void It_is_unique_across_ordinals()
    {
        Enumerable
            .Range(1, 1_000)
            .Select(ordinal => PerfFixtureDefinition.StudentUniqueIdFor(ordinal))
            .Should()
            .OnlyHaveUniqueItems();
    }

    [Test]
    public void It_rejects_non_positive_ordinals()
    {
        FluentActions
            .Invoking(() => PerfFixtureDefinition.StudentUniqueIdFor(0))
            .Should()
            .Throw<ArgumentOutOfRangeException>();
    }
}

[TestFixture]
public class Given_The_Document_Uuid_Derivation
{
    [Test]
    public void It_embeds_the_ordinal_in_the_final_hex_digits()
    {
        PerfFixtureDefinition
            .DocumentUuidFor(1)
            .Should()
            .Be(Guid.Parse("8f7a0000-0000-4000-8000-000000000001"));
        PerfFixtureDefinition
            .DocumentUuidFor(500_000)
            .Should()
            .Be(Guid.Parse("8f7a0000-0000-4000-8000-00000007a120"));
    }

    [Test]
    public void It_keeps_the_rfc_4122_shape()
    {
        string text = PerfFixtureDefinition.DocumentUuidFor(42).ToString("D");
        text[14].Should().Be('4');
        text[19].Should().Be('8');
    }

    [Test]
    public void It_is_deterministic()
    {
        PerfFixtureDefinition.DocumentUuidFor(777).Should().Be(PerfFixtureDefinition.DocumentUuidFor(777));
    }

    [Test]
    public void It_is_unique_across_ordinals()
    {
        Enumerable
            .Range(1, 1_000)
            .Select(ordinal => PerfFixtureDefinition.DocumentUuidFor(ordinal))
            .Should()
            .OnlyHaveUniqueItems();
    }

    [Test]
    public void It_rejects_non_positive_ordinals()
    {
        FluentActions
            .Invoking(() => PerfFixtureDefinition.DocumentUuidFor(0))
            .Should()
            .Throw<ArgumentOutOfRangeException>();
    }
}

[TestFixture]
public class Given_The_Descriptor_Catalog
{
    private readonly PerfFixtureDefinition _definition = new(PerfFixtureKind.Primary500k);

    [Test]
    public void It_covers_every_descriptor_backed_column_of_the_fixture_shape()
    {
        PerfFixtureDefinition
            .DescriptorResourceNames.Should()
            .Equal(
                "SexDescriptor",
                "OtherNameTypeDescriptor",
                "IdentificationDocumentUseDescriptor",
                "PersonalInformationVerificationDescriptor",
                "VisaDescriptor"
            );
        PerfFixtureDefinition.DescriptorCount.Should().Be(5);
    }

    [Test]
    public void It_places_descriptor_documents_directly_above_the_student_range()
    {
        _definition.DescriptorDocumentIdFor("SexDescriptor").Should().Be(_definition.MaxDocumentId + 1);
        _definition.DescriptorDocumentIdFor("VisaDescriptor").Should().Be(_definition.MaxDocumentId + 5);
        _definition.ReseedTargetDocumentId.Should().Be(_definition.MaxDocumentId + 5);
    }

    [Test]
    public void It_derives_descriptor_uris_the_way_the_write_path_does()
    {
        PerfFixtureDefinition
            .DescriptorNamespaceFor("VisaDescriptor")
            .Should()
            .Be("uri://ed-fi.org/VisaDescriptor");
        PerfFixtureDefinition
            .DescriptorUriFor("VisaDescriptor")
            .Should()
            .Be("uri://ed-fi.org/VisaDescriptor#Perf");
    }

    [Test]
    public void It_gives_each_descriptor_a_unique_deterministic_document_uuid()
    {
        PerfFixtureDefinition
            .DescriptorResourceNames.Select(name => _definition.DescriptorDocumentUuidFor(name))
            .Should()
            .OnlyHaveUniqueItems();
        _definition
            .DescriptorDocumentUuidFor("SexDescriptor")
            .Should()
            .Be(PerfFixtureDefinition.DocumentUuidFor(_definition.RowCount + 1));
    }

    [Test]
    public void It_rejects_resources_outside_the_catalog()
    {
        FluentActions
            .Invoking(() => _definition.DescriptorDocumentIdFor("CountryDescriptor"))
            .Should()
            .Throw<ArgumentException>();
    }
}

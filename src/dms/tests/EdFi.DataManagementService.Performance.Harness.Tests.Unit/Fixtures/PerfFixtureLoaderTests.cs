// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Loader_Chunking
{
    [Test]
    public void It_covers_the_row_range_without_overlap()
    {
        PerfFixtureLoader
            .Chunks(rowCount: 10_000, chunkSize: 3_000)
            .Should()
            .Equal((1, 3_000), (3_001, 6_000), (6_001, 9_000), (9_001, 10_000));
    }

    [Test]
    public void It_emits_one_chunk_when_the_size_covers_everything()
    {
        PerfFixtureLoader.Chunks(rowCount: 500, chunkSize: 50_000).Should().Equal((1, 500));
    }

    [Test]
    public void It_handles_an_exact_multiple()
    {
        PerfFixtureLoader
            .Chunks(rowCount: 6_000, chunkSize: 3_000)
            .Should()
            .Equal((1, 3_000), (3_001, 6_000));
    }
}

[TestFixture]
public class Given_Compact_Descriptor_Loader_Parameters
{
    private IReadOnlyList<(string Name, int Value)> _parameters = null!;

    [SetUp]
    public void Setup()
    {
        _parameters = PerfFixtureLoader.DescriptorParameters(
            new Dictionary<string, int>
            {
                [PerfFixtureDefinition.SexDescriptorResource] = 31,
                [PerfFixtureDefinition.OtherNameTypeDescriptorResource] = 47,
                [PerfFixtureDefinition.IdentificationDocumentUseDescriptorResource] = 59,
                [PerfFixtureDefinition.PersonalInformationVerificationDescriptorResource] = 71,
                [PerfFixtureDefinition.VisaDescriptorResource] = 89,
            }
        );
    }

    [Test]
    public void It_binds_the_returned_compact_ids_by_resource()
    {
        _parameters
            .Should()
            .Equal(
                (PerfFixtureLoaderParameters.BirthSexDescriptorId, 31),
                (PerfFixtureLoaderParameters.OtherNameTypeDescriptorId, 47),
                (PerfFixtureLoaderParameters.IdentificationDocumentUseDescriptorId, 59),
                (PerfFixtureLoaderParameters.PersonalInformationVerificationDescriptorId, 71),
                (PerfFixtureLoaderParameters.VisaDescriptorId, 89)
            );
    }
}

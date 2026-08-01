// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The SQL Server adapter's batch slicing. SQL Server's parameter ceiling is per command, so a batch that
/// would bind more parameters than the builder accepts has to be cut into several commands — and every
/// returned row re-attributed to the caller's batch coordinates.
/// </summary>
[TestFixture]
public class Given_MssqlNaturalKeyLookupAdapter
{
    [Test]
    public void It_keeps_a_batch_within_the_parameter_ceiling_in_a_single_command()
    {
        var mappingSet = RelationalAccessTestData.CreateNaturalKeyProbeMappingSet();
        var batch = new NaturalKeyLookupBatch(
            mappingSet,
            [
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.StudentSectionAssociationResource,
                    RelationalAccessTestData.CreateStudentSectionAssociationProbeTarget(),
                    RelationalAccessTestData.CreateSyntheticNaturalKeyEntries(100, 7)
                ),
                new DescriptorLookupGroup(
                    RelationalAccessTestData.SchoolTypeDescriptorResource,
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        ["uri://ed-fi.org/schooltypedescriptor#alternative"],
                    ])
                ),
            ]
        );

        var slices = MssqlNaturalKeyLookupAdapter.SliceBatch(batch);

        slices.Should().ContainSingle("701 parameters is well under the command ceiling");
        slices[0].Batch.Groups.Should().HaveCount(2);
        slices[0]
            .Origins.Select(origin => (origin.GroupIndex, origin.EntryOffset))
            .Should()
            .Equal((0, 0), (1, 0));
    }

    [Test]
    public void It_cuts_a_new_command_before_a_group_would_cross_the_parameter_ceiling()
    {
        var mappingSet = RelationalAccessTestData.CreateNaturalKeyProbeMappingSet();
        var batch = new NaturalKeyLookupBatch(
            mappingSet,
            [
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.StudentSectionAssociationResource,
                    RelationalAccessTestData.CreateStudentSectionAssociationProbeTarget(),
                    RelationalAccessTestData.CreateSyntheticNaturalKeyEntries(280, 7)
                ),
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSchoolProbeTarget(),
                    RelationalAccessTestData.CreateSyntheticNaturalKeyEntries(300, 1)
                ),
            ]
        );

        var slices = MssqlNaturalKeyLookupAdapter.SliceBatch(batch);

        // 280 x 7 = 1960; adding 300 more would reach 2260, past the 2098 ceiling.
        slices.Should().HaveCount(2);
        slices[0].Origins.Select(origin => origin.GroupIndex).Should().Equal(0, 1);
        slices[0].Batch.Groups[1].Entries.Should().HaveCount(138);
        slices[1].Origins.Select(origin => (origin.GroupIndex, origin.EntryOffset)).Should().Equal((1, 138));
        slices[1].Batch.Groups[0].Entries.Should().HaveCount(162);
    }

    [Test]
    public void It_splits_a_single_group_that_alone_exceeds_the_command_parameter_ceiling()
    {
        var mappingSet = RelationalAccessTestData.CreateNaturalKeyProbeMappingSet();
        var batch = new NaturalKeyLookupBatch(
            mappingSet,
            [
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.StudentSectionAssociationResource,
                    RelationalAccessTestData.CreateStudentSectionAssociationProbeTarget(),
                    RelationalAccessTestData.CreateSyntheticNaturalKeyEntries(600, 7)
                ),
            ]
        );

        var slices = MssqlNaturalKeyLookupAdapter.SliceBatch(batch);

        // 2098 / 7 = 299 entries per command.
        slices.Should().HaveCount(3);
        slices
            .Select(slice => (slice.Origins[0].EntryOffset, slice.Batch.Groups[0].Entries.Count))
            .Should()
            .Equal((0, 299), (299, 299), (598, 2));
        int[] expectedOrdinals = [.. Enumerable.Range(1, 299), .. Enumerable.Range(1, 299), 1, 2];

        slices
            .SelectMany(slice => slice.Batch.Groups[0].Entries.Select(entry => entry.Ordinal))
            .Should()
            .Equal(expectedOrdinals);
    }

    [Test]
    public void It_only_produces_slices_the_command_builder_accepts()
    {
        var mappingSet = RelationalAccessTestData.CreateNaturalKeyProbeMappingSet();
        var batch = new NaturalKeyLookupBatch(
            mappingSet,
            [
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.StudentSectionAssociationResource,
                    RelationalAccessTestData.CreateSyntheticProbeTarget(7),
                    RelationalAccessTestData.CreateSyntheticNaturalKeyEntries(600, 7)
                ),
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSyntheticProbeTarget(1),
                    RelationalAccessTestData.CreateSyntheticNaturalKeyEntries(4096, 1)
                ),
            ]
        );

        var slices = MssqlNaturalKeyLookupAdapter.SliceBatch(batch);

        foreach (var slice in slices)
        {
            var command = MssqlNaturalKeyLookupCommandBuilder.Build(slice.Batch);

            command
                .Parameters.Count.Should()
                .BeLessThanOrEqualTo(
                    MssqlNaturalKeyLookupCommandBuilder.MssqlMaxCommandParameters,
                    "every slice must be a command SQL Server would accept"
                );
        }

        slices
            .Sum(slice => slice.Batch.Groups.Sum(group => group.Entries.Count))
            .Should()
            .Be(4696, "slicing never drops or duplicates an entry");
    }
}

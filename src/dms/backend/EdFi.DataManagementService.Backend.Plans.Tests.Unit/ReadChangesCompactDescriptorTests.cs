// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_ReadChanges_Compact_Descriptor_Bases(SqlDialect dialect)
{
    private CompactDescriptorHistoryFixture _fixture = null!;
    private IReadOnlyList<ReadChangesCustomViewCheckSpec> _checks = [];

    [SetUp]
    public void Setup()
    {
        _fixture = new(dialect);
        _checks = Plan(_fixture.MappingSet).Checks;
    }

    [Test]
    public void It_compiles_each_same_named_descriptor_basis_with_its_qualified_resource_key()
    {
        _checks
            .Select(check => check.BasisResource)
            .Should()
            .Equal(
                CompactDescriptorAuthorizationFixture.EdFiKind,
                CompactDescriptorAuthorizationFixture.SampleKind
            );
        _checks
            .Select(check => check.Basis)
            .Should()
            .Equal(
                new ReadChangesCustomViewBasis.DescriptorSeek(
                    CompactDescriptorAuthorizationFixture.EdFiKind,
                    101,
                    new("OldKind_Namespace"),
                    new("OldKind_CodeValue"),
                    Probe()
                ),
                new ReadChangesCustomViewBasis.DescriptorSeek(
                    CompactDescriptorAuthorizationFixture.SampleKind,
                    202,
                    new("OldSampleKind_Namespace"),
                    new("OldSampleKind_CodeValue"),
                    Probe()
                )
            );
    }

    [Test]
    public void It_reads_document_ids_and_type_keys_from_retained_descriptor_history()
    {
        foreach (var seek in _checks.Select(check => (ReadChangesCustomViewBasis.DescriptorSeek)check.Basis))
        {
            seek.ProbeArm.Should().Be(Probe());
        }
    }

    [Test]
    public void It_rejects_a_missing_qualified_resource_key_instead_of_using_the_other_project()
    {
        MappingSet mapping = _fixture.MappingSet with
        {
            ResourceKeyIdByResource = _fixture
                .MappingSet.ResourceKeyIdByResource.Where(pair =>
                    pair.Key != CompactDescriptorAuthorizationFixture.SampleKind
                )
                .ToDictionary(),
        };
        Action act = () => Plan(mapping);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Sample.KindDescriptor*");
    }

    [Test]
    public void It_keeps_a_descriptor_self_basis_on_the_stored_document_id()
    {
        var descriptor = _fixture.Descriptor(CompactDescriptorAuthorizationFixture.SampleKind);
        var outcome = ReadChangesCustomViewPlanner.Plan(
            _fixture.MappingSet,
            descriptor,
            _fixture.DescriptorHistory,
            [Strategy(CompactDescriptorAuthorizationFixture.SampleKind, 0)]
        );
        outcome
            .Checks.Single()
            .Basis.Should()
            .Be(new ReadChangesCustomViewBasis.StoredDocumentId(new("DocumentId")));
    }

    private ReadChangesCustomViewPlanResult Plan(MappingSet mapping) =>
        ReadChangesCustomViewPlanner.Plan(
            mapping,
            _fixture.Authorization.Subject,
            _fixture.ResourceHistory,
            [
                Strategy(CompactDescriptorAuthorizationFixture.EdFiKind, 0),
                Strategy(CompactDescriptorAuthorizationFixture.SampleKind, 1),
            ]
        );

    private ReadChangesCustomViewDescriptorProbeArm Probe() =>
        new(
            _fixture.DescriptorHistory.Table,
            new("DocumentId"),
            new("ResourceKeyId"),
            new("OldNamespace"),
            new("OldCodeValue")
        );

    private static SupportedCustomViewAuthorizationStrategy Strategy(
        QualifiedResourceName resource,
        int index
    ) => new(new($"{resource.ResourceName}WithAllowedRowsIncludingDeletes", index), index, resource);
}

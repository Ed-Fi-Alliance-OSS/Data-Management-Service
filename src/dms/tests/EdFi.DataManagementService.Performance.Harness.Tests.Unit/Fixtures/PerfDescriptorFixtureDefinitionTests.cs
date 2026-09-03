// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Descriptor_Fixture_Kinds
{
    [Test]
    public void It_offers_the_final_gate_and_smoke_kinds()
    {
        PerfDescriptorFixtureKind
            .All.Select(kind => (kind.Id, kind.RowCount))
            .Should()
            .Equal(("descriptors-25k", 25_000), ("descriptors-smoke-2k", 2_000));
    }

    [Test]
    public void It_finds_kinds_by_id_and_rejects_unknown_ids()
    {
        PerfDescriptorFixtureKind
            .FindById("descriptors-25k")
            .Should()
            .Be(PerfDescriptorFixtureKind.Descriptors25k);
        PerfDescriptorFixtureKind
            .FindById(" descriptors-smoke-2k ")
            .Should()
            .Be(PerfDescriptorFixtureKind.DescriptorsSmoke2k);
        PerfDescriptorFixtureKind.FindById("primary-500k").Should().BeNull();
    }
}

[TestFixture]
public class Given_The_Descriptor_Fixture_Definition
{
    private PerfDescriptorFixtureDefinition _definition = null!;

    [SetUp]
    public void Setup()
    {
        _definition = new PerfDescriptorFixtureDefinition(PerfDescriptorFixtureKind.Descriptors25k);
    }

    [Test]
    public void It_loads_dense_document_ids()
    {
        PerfDescriptorFixtureDefinition.DocumentIdFor(1).Should().Be(1);
        PerfDescriptorFixtureDefinition.DocumentIdFor(25_000).Should().Be(25_000);
        _definition.MaxDocumentId.Should().Be(25_000);
        _definition.ReseedTargetDocumentId.Should().Be(25_000);
    }

    [Test]
    public void It_splits_the_namespaces_evenly_and_interleaved()
    {
        _definition.AccessibleCount.Should().Be(12_500);
        PerfDescriptorFixtureDefinition.IsAccessible(1).Should().BeTrue();
        PerfDescriptorFixtureDefinition.IsAccessible(2).Should().BeFalse();
        PerfDescriptorFixtureDefinition
            .NamespaceFor(3)
            .Should()
            .Be("uri://perf-accessible.ed-fi.org/AcademicSubjectDescriptor");
        PerfDescriptorFixtureDefinition
            .NamespaceFor(4)
            .Should()
            .Be("uri://perf-denied.example/AcademicSubjectDescriptor");
    }

    [Test]
    public void It_keeps_the_accessible_namespace_under_the_principal_prefix()
    {
        PerfDescriptorFixtureDefinition
            .AccessibleNamespace.Should()
            .StartWith(PerfDescriptorFixtureDefinition.AccessibleNamespacePrefix + "/");
        PerfDescriptorFixtureDefinition
            .InaccessibleNamespace.Should()
            .NotStartWith(PerfDescriptorFixtureDefinition.AccessibleNamespacePrefix);
    }

    [Test]
    public void It_derives_code_values_uris_and_uuids_from_the_ordinal()
    {
        PerfDescriptorFixtureDefinition.CodeValueFor(7).Should().Be("perf-000000007");
        PerfDescriptorFixtureDefinition
            .UriFor(7)
            .Should()
            .Be("uri://perf-accessible.ed-fi.org/AcademicSubjectDescriptor#perf-000000007");
        PerfDescriptorFixtureDefinition
            .DocumentUuidFor(255)
            .Should()
            .Be(Guid.Parse("8f7a3000-0000-4000-8000-0000000000ff"));
    }

    [Test]
    public void It_computes_the_dense_document_id_checksum()
    {
        _definition.DocumentIdSum().Should().Be(312_512_500);
        new PerfDescriptorFixtureDefinition(PerfDescriptorFixtureKind.DescriptorsSmoke2k)
            .DocumentIdSum()
            .Should()
            .Be(2_001_000);
    }

    [Test]
    public void It_keeps_the_uuid_prefix_disjoint_from_the_primary_fixture_prefixes()
    {
        PerfDescriptorFixtureDefinition
            .DocumentUuidPrefix.Should()
            .NotBe(PerfFixtureDefinition.DocumentUuidPrefix);
        PerfDescriptorFixtureDefinition
            .DocumentUuidPrefix.Should()
            .NotBe(PerfAuthorizationSeedDefinition.SsaDocumentUuidPrefix);
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_ReferenceResolverIntegrationFixture
{
    private ReferenceResolverIntegrationFixture _fixture = null!;
    private IReadOnlyList<ReferenceResolverSeedTableBatch> _seedBatches = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = ReferenceResolverIntegrationFixture.CreateDefault();
        _seedBatches = _fixture.SeedData.CreateTableBatches();
    }

    [Test]
    public void It_describes_the_required_dms_seed_batches_for_shared_integration_harnesses()
    {
        _seedBatches
            .Select(batch => $"{batch.Table.Schema.Value}.{batch.Table.Name}")
            .Should()
            .Equal(
                "dms.ResourceKey",
                "dms.Document",
                "dms.ReferentialIdentity",
                "edfi.School",
                "edfi.LocalEducationAgency",
                "edfi.EducationOrganizationIdentity",
                "edfi.WideIdentityResource",
                "dms.Descriptor"
            );

        _seedBatches.Single(batch => batch.Table.Name == "ResourceKey").Rows.Should().HaveCount(7);
        _seedBatches.Single(batch => batch.Table.Name == "Document").Rows.Should().HaveCount(5);
        _seedBatches.Single(batch => batch.Table.Name == "ReferentialIdentity").Rows.Should().HaveCount(6);
        _seedBatches.Single(batch => batch.Table.Name == "School").Rows.Should().HaveCount(1);
        _seedBatches.Single(batch => batch.Table.Name == "LocalEducationAgency").Rows.Should().HaveCount(1);
        _seedBatches
            .Single(batch => batch.Table.Name == "EducationOrganizationIdentity")
            .Rows.Should()
            .HaveCount(1);
        _seedBatches.Single(batch => batch.Table.Name == "WideIdentityResource").Rows.Should().HaveCount(1);
        _seedBatches.Single(batch => batch.Table.Name == "Descriptor").Rows.Should().HaveCount(2);

        _fixture
            .SeedData.ResourceKeys.Should()
            .Contain(resourceKey =>
                resourceKey.Resource == _fixture.EducationOrganizationResource
                && resourceKey.ResourceKeyId == 30
                && resourceKey.IsAbstractResource
            );
        _fixture
            .SeedData.ReferentialIdentities.Should()
            .Contain(identity =>
                identity.ReferentialId == _fixture.EducationOrganizationAliasReferentialId
                && identity.DocumentId == 101
                && identity.ResourceKeyId == 30
            );

        var abstractUnionView = _fixture
            .CreateMappingSet(EdFi.DataManagementService.Backend.External.SqlDialect.Mssql)
            .Model.AbstractUnionViewsInNameOrder.Single();
        abstractUnionView
            .OutputColumnsInSelectOrder.Select(column => column.ColumnName.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId");
        abstractUnionView
            .UnionArmsInOrder.SelectMany(arm => arm.ProjectionExpressionsInSelectOrder)
            .Should()
            .AllBeOfType<AbstractUnionViewProjectionExpression.SourceColumn>();
    }

    [Test]
    public void It_describes_the_natural_key_probe_surface_the_new_resolver_seeks()
    {
        var mappingSet = _fixture.CreateMappingSet(
            EdFi.DataManagementService.Backend.External.SqlDialect.Pgsql
        );

        mappingSet
            .NaturalKeyProbeTargets.Keys.Select(resource => resource.ResourceName)
            .Should()
            .BeEquivalentTo(
                "School",
                "LocalEducationAgency",
                "EducationOrganization",
                "WideIdentityResource"
            );

        var educationOrganizationProbe = mappingSet.NaturalKeyProbeTargets[
            _fixture.EducationOrganizationResource
        ];
        educationOrganizationProbe.IsAbstract.Should().BeTrue();
        educationOrganizationProbe
            .ProbeTable.Name.Should()
            .Be("EducationOrganizationIdentity", "abstract probes seek the identity table, not the view");

        mappingSet
            .NaturalKeyProbeTargets[_fixture.WideIdentityResource]
            .Columns.Select(column => (column.StorageColumn.Value, column.ScalarType.Kind))
            .Should()
            .Equal(
                ("Int64Key", ScalarKind.Int64),
                ("DecimalKey", ScalarKind.Decimal),
                ("DateKey", ScalarKind.Date),
                ("DateTimeKey", ScalarKind.DateTime),
                ("BooleanKey", ScalarKind.Boolean),
                ("StringKey", ScalarKind.String)
            );

        mappingSet
            .DescriptorProbeTarget.DiscriminatorLiteralByResource.Should()
            .ContainKey(_fixture.SchoolTypeDescriptorResource)
            .WhoseValue.Should()
            .Be("SchoolTypeDescriptor");
    }

    [Test]
    public void It_declares_a_reference_key_unique_constraint_for_every_probe_target()
    {
        var mappingSet = _fixture.CreateMappingSet(
            EdFi.DataManagementService.Backend.External.SqlDialect.Pgsql
        );

        foreach (var (resource, probe) in mappingSet.NaturalKeyProbeTargets)
        {
            var tableModel = mappingSet
                .Model.ConcreteResourcesInNameOrder.Select(concrete => concrete.RelationalModel.Root)
                .Concat(mappingSet.Model.AbstractIdentityTablesInNameOrder.Select(table => table.TableModel))
                .Single(table => table.Table == probe.ProbeTable);

            tableModel
                .Constraints.OfType<TableConstraint.Unique>()
                .Select(unique => unique.Columns.Select(column => column.Value).ToArray())
                .Should()
                .ContainEquivalentOf(
                    probe
                        .Columns.Select(column => column.StorageColumn.Value)
                        .Append(probe.DocumentIdColumn.Value)
                        .ToArray(),
                    because: $"the probe for '{resource.ResourceName}' must have an index to seek"
                );
        }
    }
}

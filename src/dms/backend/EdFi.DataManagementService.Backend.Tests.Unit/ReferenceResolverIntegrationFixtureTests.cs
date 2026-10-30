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
                "edfi.School",
                "edfi.LocalEducationAgency",
                "dms.Descriptor"
            );

        _seedBatches.Single(batch => batch.Table.Name == "ResourceKey").Rows.Should().HaveCount(6);
        _seedBatches.Single(batch => batch.Table.Name == "Document").Rows.Should().HaveCount(4);
        _seedBatches.Single(batch => batch.Table.Name == "School").Rows.Should().HaveCount(1);
        _seedBatches.Single(batch => batch.Table.Name == "LocalEducationAgency").Rows.Should().HaveCount(1);
        _seedBatches.Single(batch => batch.Table.Name == "Descriptor").Rows.Should().HaveCount(2);

        _fixture
            .SeedData.ResourceKeys.Should()
            .Contain(resourceKey =>
                resourceKey.Resource == new QualifiedResourceName("Ed-Fi", "EducationOrganization")
                && resourceKey.ResourceKeyId == 30
                && resourceKey.IsAbstractResource
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
    public void It_omits_generated_descriptor_keys_and_removed_stored_columns_from_seed_inserts()
    {
        var batch = _seedBatches.Single(batch => batch.Table.Name == "Descriptor");
        batch
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription");
        batch
            .Rows[0]
            .Should()
            .Equal(303L, (short)13, "uri://ed-fi.org/SchoolTypeDescriptor", "Alternative", "Alternative");
    }
}

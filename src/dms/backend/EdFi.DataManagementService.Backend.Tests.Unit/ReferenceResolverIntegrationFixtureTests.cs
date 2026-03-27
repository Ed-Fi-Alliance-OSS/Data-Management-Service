// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
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
                "dms.Descriptor"
            );

        _seedBatches.Single(batch => batch.Table.Name == "ResourceKey").Rows.Should().HaveCount(6);
        _seedBatches.Single(batch => batch.Table.Name == "Document").Rows.Should().HaveCount(4);
        _seedBatches.Single(batch => batch.Table.Name == "ReferentialIdentity").Rows.Should().HaveCount(5);
        _seedBatches.Single(batch => batch.Table.Name == "School").Rows.Should().HaveCount(1);
        _seedBatches.Single(batch => batch.Table.Name == "LocalEducationAgency").Rows.Should().HaveCount(1);
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
    public async Task It_can_drive_successful_and_fail_closed_reference_resolution_scenarios()
    {
        var adapter = new RecordingReferenceResolverAdapter([
            [
                new ReferenceLookupResult(
                    _fixture.SchoolReferentialId,
                    101L,
                    11,
                    11,
                    false,
                    "$$.schoolId=255901"
                ),
                new ReferenceLookupResult(
                    _fixture.EducationOrganizationAliasReferentialId,
                    101L,
                    11,
                    30,
                    false,
                    "$$.educationOrganizationId=255901"
                ),
                new ReferenceLookupResult(
                    _fixture.LocalEducationAgencyReferentialId,
                    202L,
                    12,
                    12,
                    false,
                    "$$.schoolId=255901"
                ),
                new ReferenceLookupResult(
                    _fixture.SchoolTypeDescriptorReferentialId,
                    303L,
                    13,
                    13,
                    true,
                    "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative"
                ),
                new ReferenceLookupResult(
                    _fixture.AcademicSubjectDescriptorReferentialId,
                    404L,
                    14,
                    14,
                    true,
                    "$$.descriptor=uri://ed-fi.org/academicsubjectdescriptor#english"
                ),
            ],
        ]);
        var sut = new ReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            new ReferenceResolverRequest(
                MappingSet: _fixture.CreateMappingSet(
                    EdFi.DataManagementService.Backend.External.SqlDialect.Pgsql
                ),
                RequestResource: _fixture.RequestResource,
                DocumentReferences:
                [
                    _fixture.CreateSchoolReference("$.schoolReference"),
                    _fixture.CreateEducationOrganizationReference("$.educationOrganizationReference"),
                    _fixture.CreateSchoolReference(
                        "$.localEducationAgencyReference",
                        _fixture.LocalEducationAgencyReferentialId
                    ),
                    _fixture.CreateSchoolReference(
                        "$.sections[0].schoolReference",
                        _fixture.MissingSchoolReferentialId
                    ),
                    _fixture.CreateSchoolReference(
                        "$.sections[1].schoolReference",
                        _fixture.MissingSchoolReferentialId
                    ),
                ],
                DescriptorReferences:
                [
                    _fixture.CreateSchoolTypeDescriptorReference("$.schoolTypeDescriptor"),
                    _fixture.CreateSchoolTypeDescriptorReference(
                        "$.alternateSchoolTypeDescriptor",
                        _fixture.AcademicSubjectDescriptorReferentialId,
                        _fixture.AcademicSubjectDescriptorUri
                    ),
                    _fixture.CreateSchoolTypeDescriptorReference(
                        "$.programs[0].schoolTypeDescriptor",
                        _fixture.MissingSchoolTypeDescriptorReferentialId,
                        _fixture.MissingSchoolTypeDescriptorUri
                    ),
                ]
            )
        );

        result
            .SuccessfulDocumentReferencesByPath.Keys.Should()
            .Equal(new JsonPath("$.schoolReference"), new JsonPath("$.educationOrganizationReference"));
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.educationOrganizationReference")]
            .ResourceKeyId.Should()
            .Be(11);
        result
            .LookupsByReferentialId[_fixture.EducationOrganizationAliasReferentialId]
            .Result!.ReferentialIdentityResourceKeyId.Should()
            .Be(30);

        result
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.localEducationAgencyReference", DocumentReferenceFailureReason.IncompatibleTargetType),
                ("$.sections[0].schoolReference", DocumentReferenceFailureReason.Missing),
                ("$.sections[1].schoolReference", DocumentReferenceFailureReason.Missing)
            );

        result
            .SuccessfulDescriptorReferencesByPath.Keys.Should()
            .Equal(new JsonPath("$.schoolTypeDescriptor"));
        result
            .InvalidDescriptorReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.alternateSchoolTypeDescriptor", DescriptorReferenceFailureReason.DescriptorTypeMismatch),
                ("$.programs[0].schoolTypeDescriptor", DescriptorReferenceFailureReason.Missing)
            );
    }

    private sealed class RecordingReferenceResolverAdapter(
        IReadOnlyList<IReadOnlyList<ReferenceLookupResult>> responses
    ) : IReferenceResolverAdapter
    {
        private readonly Queue<IReadOnlyList<ReferenceLookupResult>> _responses = new(responses);

        public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
            ReferenceLookupRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (!_responses.TryDequeue(out var response))
            {
                throw new AssertionException(
                    "No fake adapter response was configured for this resolver call."
                );
            }

            return Task.FromResult(response);
        }
    }
}

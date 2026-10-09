// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_ReferenceLookupVerificationSupport_When_reusing_projection_metadata
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");

    private IReadOnlyList<ReferenceLookupVerificationProjection> _schoolOnlyProjections = null!;
    private IReadOnlyList<ReferenceLookupVerificationProjection> _schoolAndEdOrgProjections = null!;

    [SetUp]
    public void Setup()
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(_requestResource);

        _schoolOnlyProjections = ReferenceLookupVerificationSupport.BuildProjections(
            new ReferenceLookupRequest(
                MappingSet: mappingSet,
                RequestResource: _requestResource,
                Lookups:
                [
                    RelationalAccessTestData.CreateSchoolLookup(
                        ReferenceLookupVerificationSupportTestData.CreateReferentialId(1)
                    ),
                ]
            )
        );
        _schoolAndEdOrgProjections = ReferenceLookupVerificationSupport.BuildProjections(
            new ReferenceLookupRequest(
                MappingSet: mappingSet,
                RequestResource: _requestResource,
                Lookups:
                [
                    RelationalAccessTestData.CreateSchoolLookup(
                        ReferenceLookupVerificationSupportTestData.CreateReferentialId(2)
                    ),
                    RelationalAccessTestData.CreateEducationOrganizationLookup(
                        ReferenceLookupVerificationSupportTestData.CreateReferentialId(3)
                    ),
                ]
            )
        );
    }

    [Test]
    public void It_caches_projection_metadata_by_mapping_set_and_resource_identity_shape()
    {
        _schoolOnlyProjections.Should().ContainSingle();
        _schoolAndEdOrgProjections.Should().HaveCount(2);
        _schoolAndEdOrgProjections[0].Should().BeSameAs(_schoolOnlyProjections[0]);
    }
}

[TestFixture]
public class Given_ReferenceLookupVerificationSupport_When_duplicate_resource_identity_shape_mismatches
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(_requestResource);

        try
        {
            ReferenceLookupVerificationSupport.BuildProjections(
                new ReferenceLookupRequest(
                    MappingSet: mappingSet,
                    RequestResource: _requestResource,
                    Lookups:
                    [
                        RelationalAccessTestData.CreateSchoolLookup(
                            ReferenceLookupVerificationSupportTestData.CreateReferentialId(1)
                        ),
                        CreateSchoolLookupWithIdentityPath(
                            ReferenceLookupVerificationSupportTestData.CreateReferentialId(2),
                            "$.alternateSchoolId"
                        ),
                    ]
                )
            );
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    [Test]
    public void It_rejects_duplicate_resource_lookups_with_different_identity_path_orderings()
    {
        _exception
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Match(
                "*Reference lookup verification metadata lookup failed for target 'Ed-Fi.School': "
                    + "multiple lookup entries for the same resource used different identity path orderings.*"
            );
    }

    private static ReferenceLookupRequestEntry CreateSchoolLookupWithIdentityPath(
        ReferentialId referentialId,
        string identityJsonPath
    )
    {
        var requestedIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath(identityJsonPath), "255901"),
        ]);

        return new ReferenceLookupRequestEntry(
            referentialId,
            _schoolResource,
            requestedIdentity,
            ReferenceLookupVerificationSupport.BuildExpectedVerificationIdentityKey(requestedIdentity)
        );
    }
}

internal static class ReferenceLookupVerificationSupportTestData
{
    public static ReferentialId CreateReferentialId(int seed)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        BitConverter.GetBytes(seed * 31).CopyTo(bytes, 4);

        return new ReferentialId(new Guid(bytes));
    }
}

[TestFixture(SqlDialect.Pgsql, false)]
[TestFixture(SqlDialect.Mssql, false)]
[TestFixture(SqlDialect.Pgsql, true)]
[TestFixture(SqlDialect.Mssql, true)]
public class Given_ReferenceLookupVerificationSupport_With_Unified_Descriptor_Reference_Identity(
    SqlDialect dialect,
    bool superclass
)
{
    private const string Uri = "uri://ed-fi.org/programtypedescriptor #regular#plus";
    private const string Witness =
        "$.programTypeDescriptor=" + Uri + "#$.programReference.programTypeDescriptor=" + Uri;
    private ReferenceLookupRequest _request = null!;
    private ReferenceLookupVerificationProjection _projection = null!;
    private RelationalCommand _command = null!;
    private ReferentialId _referentialId;

    [SetUp]
    public void Setup()
    {
        var model = CompactDescriptorReferentialIdentityTestModel.Build(
            dialect,
            unifiedReferenceIdentity: true
        );
        var mappingSet = new MappingSet(
            new("hash", dialect, "v3"),
            model,
            new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            model.EffectiveSchema.ResourceKeysInIdOrder.ToDictionary(
                key => key.Resource,
                key => key.ResourceKeyId
            ),
            model.EffectiveSchema.ResourceKeysInIdOrder.ToDictionary(key => key.ResourceKeyId),
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );
        var requestedResource = new QualifiedResourceName(
            "Ed-Fi",
            superclass ? "GeneralProgramOffering" : "ProgramOffering"
        );
        var identity = CompactDescriptorReferentialIdentityTestModel.CreateIdentity(
            "URI://Ed-Fi.Org/ProgramTypeDescriptor #Regular#Plus"
        );
        _referentialId = ReferentialIdFactory.Create(
            new(new("Ed-Fi"), new(requestedResource.ResourceName), false),
            identity
        );
        _request = new(
            mappingSet,
            requestedResource,
            [
                new(
                    _referentialId,
                    requestedResource,
                    identity,
                    ReferenceLookupVerificationSupport.BuildExpectedVerificationIdentityKey(identity)
                ),
            ]
        );
        _projection = ReferenceLookupVerificationSupport.BuildProjections(_request).Single();
        _command =
            dialect is SqlDialect.Pgsql
                ? PostgresqlReferenceLookupCommandBuilder.Build(_request)
                : MssqlReferenceLookupSmallListStrategy.BuildCommand(_request);
    }

    [Test]
    public void It_retains_exact_core_hash_and_witness_text_for_mixed_case_copied_descriptor_identities()
    {
        _request.Lookups[0].ExpectedVerificationIdentityKey.Should().Be(Witness);
        _referentialId
            .Value.Should()
            .Be(
                new Guid(
                    superclass
                        ? "238d2798-a107-5f1b-9341-539d8282aeba"
                        : "4f625532-5eef-514b-96a7-76860719b783"
                )
            );
    }

    [Test]
    public void It_preserves_identity_path_order_and_compact_type_metadata_in_both_projections()
    {
        _projection
            .IdentityElements.Select(element => element.IdentityJsonPath)
            .Should()
            .Equal("$.programTypeDescriptor", "$.programReference.programTypeDescriptor");
        _projection
            .IdentityElements.Select(element => element.Column.Value)
            .Should()
            .Equal("ProgramTypeDescriptor_DescriptorId", "Program_ProgramTypeDescriptor_DescriptorId");
        _projection
            .IdentityElements.Select(element => element.ScalarType.Kind)
            .Should()
            .Equal(ScalarKind.Int32, ScalarKind.Int32);
        _projection
            .IdentityElements.Select(element => element.IsDescriptorReference)
            .Should()
            .Equal(true, true);
        _projection.ResourceKeyId.Should().Be(superclass ? (short)2 : (short)1);
    }

    [Test]
    public void It_reconstructs_the_whole_uri_through_compact_joins_for_each_witness_element()
    {
        var tableName = superclass ? "GeneralProgramOffering_View" : "ProgramOffering";
        _command
            .CommandText.Should()
            .Contain(
                dialect is SqlDialect.Pgsql
                    ? $"FROM \"edfi\".\"{tableName}\" source"
                    : $"FROM [edfi].[{tableName}] source"
            );
        foreach (var element in _projection.IdentityElements)
        {
            if (dialect is SqlDialect.Pgsql)
            {
                _command.CommandText.Should().Contain("'" + element.IdentityJsonPath + "=' || lower((");
                _command
                    .CommandText.Should()
                    .Contain("SELECT descriptor.\"Namespace\" || '#' || descriptor.\"CodeValue\"");
                _command
                    .CommandText.Should()
                    .Contain("descriptor.\"DescriptorId\" = source.\"" + element.Column.Value + "\"");
            }
            else
            {
                _command.CommandText.Should().Contain("N'" + element.IdentityJsonPath + "=' + LOWER((");
                _command
                    .CommandText.Should()
                    .Contain("SELECT descriptor.[Namespace] + N'#' + descriptor.[CodeValue]");
                _command
                    .CommandText.Should()
                    .Contain("descriptor.[DescriptorId] = source.[" + element.Column.Value + "]");
            }
        }
        _command
            .CommandText.Should()
            .NotContain(dialect is SqlDialect.Pgsql ? "descriptor.\"Uri\"" : "descriptor.[Uri]");
        _command
            .CommandText.Should()
            .Contain(
                dialect is SqlDialect.Pgsql
                    ? "descriptor.\"DocumentId\" = document.\"DocumentId\""
                    : "descriptor.[DocumentId] = document.[DocumentId]"
            );
    }

    [Test]
    public async Task It_accepts_the_exact_copied_descriptor_witness_and_preserves_the_owning_document_id()
    {
        var result = await ResolveDocumentReference(Witness);

        result.HasFailures.Should().BeFalse();
        result
            .SuccessfulDocumentReferencesByPath[new("$.programOfferingReference")]
            .DocumentId.Should()
            .Be(3_000_000_001L);
        result.LookupsByReferentialId[_referentialId].Result!.VerificationIdentityKey.Should().Be(Witness);
    }

    [TestCase("uri://ed-fi.org/programtypedescriptor#regular#plus")]
    [TestCase("URI://ED-FI.ORG/PROGRAMTYPEDESCRIPTOR #REGULAR#PLUS")]
    public async Task It_rejects_a_witness_that_changes_spacing_or_casing_inside_the_copied_identity(
        string storedUri
    )
    {
        var storedWitness =
            "$.programTypeDescriptor=" + Uri + "#$.programReference.programTypeDescriptor=" + storedUri;
        var act = async () => await ResolveDocumentReference(storedWitness);

        var exception = await act.Should().ThrowAsync<ReferenceLookupCorruptionException>();
        exception.Which.Message.Should().Contain(Witness);
        exception.Which.Message.Should().Contain(storedWitness);
    }

    private Task<ResolvedReferenceSet> ResolveDocumentReference(string storedWitness)
    {
        var executor = new InMemoryRelationalCommandExecutor(
            [
                new InMemoryRelationalCommandExecution([
                    InMemoryRelationalResultSet.Create(
                        RelationalAccessTestData.CreateRow(
                            ("ReferentialId", _referentialId.Value),
                            ("DocumentId", 3_000_000_001L),
                            ("ResourceKeyId", (short)1),
                            ("ReferentialIdentityResourceKeyId", superclass ? (short)2 : (short)1),
                            ("DescriptorId", null),
                            ("VerificationIdentityKey", storedWitness)
                        )
                    ),
                ]),
            ],
            dialect
        );
        IReferenceResolverAdapter adapter =
            dialect is SqlDialect.Pgsql
                ? new PostgresqlReferenceResolverAdapter(executor)
                : new MssqlReferenceResolverAdapter(executor);
        var lookup = _request.Lookups[0];

        return new ReferenceResolver(adapter).ResolveAsync(
            new(
                _request.MappingSet,
                _request.RequestResource,
                [
                    new DocumentReference(
                        new(
                            new(lookup.RequestedResource.ProjectName),
                            new(lookup.RequestedResource.ResourceName),
                            false
                        ),
                        lookup.RequestedIdentity,
                        lookup.ReferentialId,
                        new("$.programOfferingReference")
                    ),
                ],
                []
            )
        );
    }
}

[TestFixture]
public class Given_ReferenceLookupVerificationSupport_With_Direct_Descriptor_Identity
{
    [TestCase(
        "URI://Ed-Fi.Org/ProgramTypeDescriptor#Regular#Plus",
        "uri://ed-fi.org/programtypedescriptor#regular#plus",
        "b13cae2e-8184-5355-ba0e-d78d0eb3ff02"
    )]
    [TestCase(
        "URI://Ed-Fi.Org/ProgramTypeDescriptor #Regular#Plus",
        "uri://ed-fi.org/programtypedescriptor #regular#plus",
        "0b8cb18e-4a93-5bdb-96d3-a9ca16cbba96"
    )]
    public void It_preserves_whole_string_canonicalization_and_exact_ri_and_witness_values(
        string incomingUri,
        string canonicalUri,
        string expectedReferentialId
    )
    {
        var identity = CompactDescriptorReferentialIdentityTestModel.CreateDescriptorIdentity(incomingUri);
        var witness = ReferenceLookupVerificationSupport.BuildExpectedVerificationIdentityKey(
            new([new(DocumentIdentity.DescriptorIdentityJsonPath, incomingUri)]),
            normalizeDescriptorValues: true
        );
        var referentialId = ReferentialIdFactory.Create(
            new(new("Ed-Fi"), new("ProgramTypeDescriptor"), true),
            identity
        );

        witness.Should().Be("$.descriptor=" + canonicalUri);
        referentialId.Value.Should().Be(new Guid(expectedReferentialId));
    }
}

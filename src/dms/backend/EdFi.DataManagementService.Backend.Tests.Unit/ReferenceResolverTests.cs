// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_ReferenceResolver
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");

    [Test]
    public async Task It_deduplicates_referential_ids_within_a_single_request()
    {
        var documentReferentialId = new ReferentialId(Guid.NewGuid());
        var descriptorReferentialId = new ReferentialId(Guid.NewGuid());
        var mappingSet = CreateMappingSet();
        var adapter = new RecordingReferenceResolverAdapter([
            [
                new ReferenceLookupResult(
                    ReferentialId: documentReferentialId,
                    DocumentId: 101,
                    ResourceKeyId: 11,
                    IsDescriptor: false
                ),
                new ReferenceLookupResult(
                    ReferentialId: descriptorReferentialId,
                    DocumentId: 202,
                    ResourceKeyId: 12,
                    IsDescriptor: true
                ),
            ],
        ]);

        var sut = new ReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            new ReferenceResolverRequest(
                MappingSet: mappingSet,
                RequestResource: _requestResource,
                DocumentReferences:
                [
                    CreateDocumentReference(documentReferentialId, "$.schoolReference"),
                    CreateDocumentReference(documentReferentialId, "$.educationOrganizationReference"),
                ],
                DescriptorReferences:
                [
                    CreateDescriptorReference(
                        descriptorReferentialId,
                        "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
                        "$.schoolTypeDescriptor"
                    ),
                    CreateDescriptorReference(
                        descriptorReferentialId,
                        "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
                        "$.programs[0].schoolTypeDescriptor"
                    ),
                ]
            )
        );

        adapter.Requests.Should().ContainSingle();
        adapter.Requests[0].MappingSet.Should().BeSameAs(mappingSet);
        adapter.Requests[0].RequestResource.Should().Be(_requestResource);
        adapter.Requests[0].ReferentialIds.Should().Equal(documentReferentialId, descriptorReferentialId);

        result
            .SuccessfulDocumentReferencesByPath.Keys.Should()
            .Equal(new JsonPath("$.schoolReference"), new JsonPath("$.educationOrganizationReference"));
        result
            .SuccessfulDocumentReferencesByPath.Values.Select(reference => reference.DocumentId)
            .Should()
            .Equal(101L, 101L);
        result.DocumentReferenceOccurrences.Should().HaveCount(2);
        result
            .DocumentReferenceOccurrences.Select(occurrence => occurrence.Lookup.Result?.DocumentId)
            .Should()
            .Equal(101L, 101L);

        result
            .SuccessfulDescriptorReferencesByPath.Keys.Should()
            .Equal(
                new JsonPath("$.schoolTypeDescriptor"),
                new JsonPath("$.programs[0].schoolTypeDescriptor")
            );
        result
            .SuccessfulDescriptorReferencesByPath.Values.Select(reference => reference.DocumentId)
            .Should()
            .Equal(202L, 202L);
        result.DescriptorReferenceOccurrences.Should().HaveCount(2);
        result
            .DescriptorReferenceOccurrences.Select(occurrence => occurrence.Lookup.Result?.DocumentId)
            .Should()
            .Equal(202L, 202L);
    }

    [Test]
    public async Task It_memoizes_lookups_across_calls_within_the_same_request_scope()
    {
        var firstReferentialId = new ReferentialId(Guid.NewGuid());
        var secondReferentialId = new ReferentialId(Guid.NewGuid());
        var thirdReferentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingReferenceResolverAdapter([
            [
                new ReferenceLookupResult(
                    ReferentialId: firstReferentialId,
                    DocumentId: 101,
                    ResourceKeyId: 11,
                    IsDescriptor: false
                ),
                new ReferenceLookupResult(
                    ReferentialId: secondReferentialId,
                    DocumentId: 202,
                    ResourceKeyId: 12,
                    IsDescriptor: false
                ),
            ],
            [
                new ReferenceLookupResult(
                    ReferentialId: thirdReferentialId,
                    DocumentId: 303,
                    ResourceKeyId: 13,
                    IsDescriptor: false
                ),
            ],
        ]);

        var sut = new ReferenceResolver(adapter);
        var mappingSet = CreateMappingSet();

        await sut.ResolveAsync(
            new ReferenceResolverRequest(
                MappingSet: mappingSet,
                RequestResource: _requestResource,
                DocumentReferences:
                [
                    CreateDocumentReference(firstReferentialId, "$.schoolReference"),
                    CreateDocumentReference(secondReferentialId, "$.educationOrganizationReference"),
                ],
                DescriptorReferences: []
            )
        );

        var secondResult = await sut.ResolveAsync(
            new ReferenceResolverRequest(
                MappingSet: mappingSet,
                RequestResource: _requestResource,
                DocumentReferences:
                [
                    CreateDocumentReference(secondReferentialId, "$.educationServiceCenterReference"),
                    CreateDocumentReference(thirdReferentialId, "$.localEducationAgencyReference"),
                ],
                DescriptorReferences: []
            )
        );

        adapter.Requests.Should().HaveCount(2);
        adapter.Requests[0].ReferentialIds.Should().Equal(firstReferentialId, secondReferentialId);
        adapter.Requests[1].ReferentialIds.Should().Equal(thirdReferentialId);

        secondResult
            .SuccessfulDocumentReferencesByPath.Keys.Should()
            .Equal(
                new JsonPath("$.educationServiceCenterReference"),
                new JsonPath("$.localEducationAgencyReference")
            );
        secondResult
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.educationServiceCenterReference")]
            .DocumentId.Should()
            .Be(202L);
        secondResult
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.localEducationAgencyReference")]
            .DocumentId.Should()
            .Be(303L);
    }

    [Test]
    public async Task It_preserves_per_occurrence_diagnostics_while_materializing_success_maps()
    {
        var resolvedDocumentReferentialId = new ReferentialId(Guid.NewGuid());
        var missingDocumentReferentialId = new ReferentialId(Guid.NewGuid());
        var nonDescriptorReferentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingReferenceResolverAdapter([
            [
                new ReferenceLookupResult(
                    ReferentialId: resolvedDocumentReferentialId,
                    DocumentId: 101,
                    ResourceKeyId: 11,
                    IsDescriptor: false
                ),
                new ReferenceLookupResult(
                    ReferentialId: nonDescriptorReferentialId,
                    DocumentId: 404,
                    ResourceKeyId: 14,
                    IsDescriptor: false
                ),
            ],
        ]);

        var sut = new ReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            new ReferenceResolverRequest(
                MappingSet: CreateMappingSet(),
                RequestResource: _requestResource,
                DocumentReferences:
                [
                    CreateDocumentReference(resolvedDocumentReferentialId, "$.schoolReference"),
                    CreateDocumentReference(missingDocumentReferentialId, "$.sections[0].schoolReference"),
                    CreateDocumentReference(missingDocumentReferentialId, "$.sections[1].schoolReference"),
                ],
                DescriptorReferences:
                [
                    CreateDescriptorReference(
                        nonDescriptorReferentialId,
                        "URI://ED-FI.ORG/SchoolTypeDescriptor#Alternative",
                        "$.schoolTypeDescriptor"
                    ),
                ]
            )
        );

        result.SuccessfulDocumentReferencesByPath.Should().ContainSingle();
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.schoolReference")]
            .DocumentId.Should()
            .Be(101L);
        result.SuccessfulDocumentReferencesByPath.Keys.Should().Equal(new JsonPath("$.schoolReference"));
        result.LookupsByReferentialId[missingDocumentReferentialId].Result.Should().BeNull();

        result
            .DocumentReferenceOccurrences.Where(occurrence =>
                occurrence.Reference.ReferentialId == missingDocumentReferentialId
            )
            .Should()
            .HaveCount(2);

        result
            .DocumentReferenceOccurrences.Where(occurrence =>
                occurrence.Reference.ReferentialId == missingDocumentReferentialId
            )
            .Select(occurrence => occurrence.Lookup.Result)
            .Should()
            .AllSatisfy(result => result.Should().BeNull());

        result.SuccessfulDescriptorReferencesByPath.Should().BeEmpty();
        result.LookupsByReferentialId[nonDescriptorReferentialId].Result.Should().NotBeNull();
        result.LookupsByReferentialId[nonDescriptorReferentialId].Result!.IsDescriptor.Should().BeFalse();
        result.DescriptorReferenceOccurrences.Should().ContainSingle();
    }

    private static MappingSet CreateMappingSet()
    {
        const string EffectiveSchemaHash = "test-hash";

        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: EffectiveSchemaHash,
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder:
            [
                new ResourceKeyEntry(
                    ResourceKeyId: 1,
                    Resource: _requestResource,
                    ResourceVersion: "1.0",
                    IsAbstractResource: false
                ),
            ]
        );

        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchema,
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder: [],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey(EffectiveSchemaHash, SqlDialect.Pgsql, "v1"),
            Model: modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short> { [_requestResource] = 1 },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [1] = new ResourceKeyEntry(1, _requestResource, "1.0", false),
            }
        );
    }

    private static DocumentReference CreateDocumentReference(ReferentialId referentialId, string path) =>
        new(
            ResourceInfo: new BaseResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("School"),
                IsDescriptor: false
            ),
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
            ]),
            ReferentialId: referentialId,
            Path: new JsonPath(path)
        );

    private static DescriptorReference CreateDescriptorReference(
        ReferentialId referentialId,
        string uri,
        string path
    ) =>
        new(
            ResourceInfo: new BaseResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("SchoolTypeDescriptor"),
                IsDescriptor: true
            ),
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri),
            ]),
            ReferentialId: referentialId,
            Path: new JsonPath(path)
        );

    private sealed class RecordingReferenceResolverAdapter(
        IReadOnlyList<IReadOnlyList<ReferenceLookupResult>> responses
    ) : IReferenceResolverAdapter
    {
        private readonly Queue<IReadOnlyList<ReferenceLookupResult>> _responses = new(responses);

        public List<ReferenceLookupRequest> Requests { get; } = [];

        public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
            ReferenceLookupRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);

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

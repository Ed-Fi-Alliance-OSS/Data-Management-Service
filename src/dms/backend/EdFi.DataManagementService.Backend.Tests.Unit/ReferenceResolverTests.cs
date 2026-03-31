// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_ReferenceResolver
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _localEducationAgencyResource = new(
        "Ed-Fi",
        "LocalEducationAgency"
    );
    private static readonly QualifiedResourceName _educationOrganizationResource = new(
        "Ed-Fi",
        "EducationOrganization"
    );
    private static readonly QualifiedResourceName _meetingResource = new("Ed-Fi", "Meeting");
    private static readonly QualifiedResourceName _schoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );
    private static readonly QualifiedResourceName _academicSubjectDescriptorResource = new(
        "Ed-Fi",
        "AcademicSubjectDescriptor"
    );

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
                    ReferentialIdentityResourceKeyId: 11,
                    IsDescriptor: false,
                    VerificationIdentityKey: "$$.schoolId=255901"
                ),
                new ReferenceLookupResult(
                    ReferentialId: descriptorReferentialId,
                    DocumentId: 202,
                    ResourceKeyId: 13,
                    ReferentialIdentityResourceKeyId: 13,
                    IsDescriptor: true,
                    VerificationIdentityKey: "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative"
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
        adapter
            .Requests[0]
            .Lookups.Select(lookup =>
                (lookup.RequestedResource.ResourceName, lookup.ExpectedVerificationIdentityKey)
            )
            .Should()
            .Equal(
                ("School", "$$.schoolId=255901"),
                ("SchoolTypeDescriptor", "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative")
            );

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
        result.InvalidDocumentReferences.Should().BeEmpty();
        result.InvalidDescriptorReferences.Should().BeEmpty();
        result.HasFailures.Should().BeFalse();
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
                    ReferentialIdentityResourceKeyId: 11,
                    IsDescriptor: false,
                    VerificationIdentityKey: "$$.schoolId=255901"
                ),
                new ReferenceLookupResult(
                    ReferentialId: secondReferentialId,
                    DocumentId: 202,
                    ResourceKeyId: 11,
                    ReferentialIdentityResourceKeyId: 11,
                    IsDescriptor: false,
                    VerificationIdentityKey: "$$.schoolId=255901"
                ),
            ],
            [
                new ReferenceLookupResult(
                    ReferentialId: thirdReferentialId,
                    DocumentId: 303,
                    ResourceKeyId: 11,
                    ReferentialIdentityResourceKeyId: 11,
                    IsDescriptor: false,
                    VerificationIdentityKey: "$$.schoolId=255901"
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
        secondResult.InvalidDocumentReferences.Should().BeEmpty();
    }

    [Test]
    public async Task It_does_not_raise_corruption_for_matching_canonical_datetime_identity_verification_keys()
    {
        const string MeetingDateTime = "2025-03-05T13:30:45Z";

        var referentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingReferenceResolverAdapter([
            [
                new ReferenceLookupResult(
                    ReferentialId: referentialId,
                    DocumentId: 404,
                    ResourceKeyId: 15,
                    ReferentialIdentityResourceKeyId: 15,
                    IsDescriptor: false,
                    VerificationIdentityKey: $"$$.meetingDateTime={MeetingDateTime}"
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
                    CreateMeetingDocumentReference(referentialId, "$.meetingReference", MeetingDateTime),
                ],
                DescriptorReferences: []
            )
        );

        adapter.Requests.Should().ContainSingle();
        adapter
            .Requests[0]
            .Lookups.Select(static lookup => lookup.ExpectedVerificationIdentityKey)
            .Should()
            .Equal($"$$.meetingDateTime={MeetingDateTime}");
        result.InvalidDocumentReferences.Should().BeEmpty();
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.meetingReference")]
            .DocumentId.Should()
            .Be(404L);
    }

    [Test]
    public async Task It_retries_the_same_lookup_after_an_adapter_exception_without_poisoning_request_scope()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new SequencedReferenceResolverAdapter([
            (_, _) =>
                Task.FromException<IReadOnlyList<ReferenceLookupResult>>(
                    new InvalidOperationException("Simulated lookup failure.")
                ),
            (_, _) =>
                Task.FromResult<IReadOnlyList<ReferenceLookupResult>>([
                    new ReferenceLookupResult(
                        ReferentialId: referentialId,
                        DocumentId: 101,
                        ResourceKeyId: 11,
                        ReferentialIdentityResourceKeyId: 11,
                        IsDescriptor: false,
                        VerificationIdentityKey: "$$.schoolId=255901"
                    ),
                ]),
        ]);

        var sut = new ReferenceResolver(adapter);
        var request = new ReferenceResolverRequest(
            MappingSet: CreateMappingSet(),
            RequestResource: _requestResource,
            DocumentReferences: [CreateDocumentReference(referentialId, "$.schoolReference")],
            DescriptorReferences: []
        );

        var firstAct = async () => await sut.ResolveAsync(request);

        await firstAct.Should().ThrowAsync<InvalidOperationException>();

        var secondResult = await sut.ResolveAsync(request);

        adapter.Requests.Should().HaveCount(2);
        adapter.Requests[0].ReferentialIds.Should().Equal(referentialId);
        adapter.Requests[1].ReferentialIds.Should().Equal(referentialId);
        secondResult
            .SuccessfulDocumentReferencesByPath.Keys.Should()
            .Equal(new JsonPath("$.schoolReference"));
        secondResult
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.schoolReference")]
            .DocumentId.Should()
            .Be(101L);
        secondResult.InvalidDocumentReferences.Should().BeEmpty();
    }

    [Test]
    public async Task It_retries_the_same_lookup_after_adapter_cancellation_without_poisoning_request_scope()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        var adapter = new SequencedReferenceResolverAdapter([
            (_, cancellationToken) =>
                Task.FromCanceled<IReadOnlyList<ReferenceLookupResult>>(cancellationToken),
            (_, _) =>
                Task.FromResult<IReadOnlyList<ReferenceLookupResult>>([
                    new ReferenceLookupResult(
                        ReferentialId: referentialId,
                        DocumentId: 101,
                        ResourceKeyId: 11,
                        ReferentialIdentityResourceKeyId: 11,
                        IsDescriptor: false,
                        VerificationIdentityKey: "$$.schoolId=255901"
                    ),
                ]),
        ]);

        var sut = new ReferenceResolver(adapter);
        var request = new ReferenceResolverRequest(
            MappingSet: CreateMappingSet(),
            RequestResource: _requestResource,
            DocumentReferences: [CreateDocumentReference(referentialId, "$.schoolReference")],
            DescriptorReferences: []
        );

        var firstAct = async () => await sut.ResolveAsync(request, cancellationTokenSource.Token);

        await firstAct.Should().ThrowAsync<OperationCanceledException>();

        var secondResult = await sut.ResolveAsync(request);

        adapter.Requests.Should().HaveCount(2);
        adapter.Requests[0].ReferentialIds.Should().Equal(referentialId);
        adapter.Requests[1].ReferentialIds.Should().Equal(referentialId);
        secondResult
            .SuccessfulDocumentReferencesByPath.Keys.Should()
            .Equal(new JsonPath("$.schoolReference"));
        secondResult
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.schoolReference")]
            .DocumentId.Should()
            .Be(101L);
        secondResult.InvalidDocumentReferences.Should().BeEmpty();
    }

    [Test]
    public async Task It_reuses_a_memoized_miss_only_after_the_lookup_round_completes()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingReferenceResolverAdapter([
            [],
        ]);
        var sut = new ReferenceResolver(adapter);
        var mappingSet = CreateMappingSet();

        var firstResult = await sut.ResolveAsync(
            new ReferenceResolverRequest(
                MappingSet: mappingSet,
                RequestResource: _requestResource,
                DocumentReferences: [CreateDocumentReference(referentialId, "$.schoolReference")],
                DescriptorReferences: []
            )
        );

        var secondResult = await sut.ResolveAsync(
            new ReferenceResolverRequest(
                MappingSet: mappingSet,
                RequestResource: _requestResource,
                DocumentReferences: [CreateDocumentReference(referentialId, "$.sections[0].schoolReference")],
                DescriptorReferences: []
            )
        );

        adapter.Requests.Should().ContainSingle();
        adapter.Requests[0].ReferentialIds.Should().Equal(referentialId);
        firstResult
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(("$.schoolReference", DocumentReferenceFailureReason.Missing));
        secondResult
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(("$.sections[0].schoolReference", DocumentReferenceFailureReason.Missing));
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
                    ReferentialIdentityResourceKeyId: 11,
                    IsDescriptor: false,
                    VerificationIdentityKey: "$$.schoolId=255901"
                ),
                new ReferenceLookupResult(
                    ReferentialId: nonDescriptorReferentialId,
                    DocumentId: 404,
                    ResourceKeyId: 14,
                    ReferentialIdentityResourceKeyId: 14,
                    IsDescriptor: false,
                    VerificationIdentityKey: "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative"
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
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.sections[0].schoolReference", DocumentReferenceFailureReason.Missing),
                ("$.sections[1].schoolReference", DocumentReferenceFailureReason.Missing)
            );

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
        result.InvalidDescriptorReferences.Should().ContainSingle();
        result
            .InvalidDescriptorReferences[0]
            .Reason.Should()
            .Be(DescriptorReferenceFailureReason.DescriptorTypeMismatch);
        result.HasFailures.Should().BeTrue();
    }

    [Test]
    public async Task It_treats_missing_descriptors_as_missing_even_when_the_uri_text_is_nonstandard_or_implies_another_type()
    {
        var impliedOtherTypeReferentialId = new ReferentialId(Guid.NewGuid());
        var malformedUriReferentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingReferenceResolverAdapter([
            [],
        ]);
        var sut = new ReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            new ReferenceResolverRequest(
                MappingSet: CreateMappingSet(),
                RequestResource: _requestResource,
                DocumentReferences: [],
                DescriptorReferences:
                [
                    CreateDescriptorReference(
                        impliedOtherTypeReferentialId,
                        "uri://ed-fi.org/AcademicSubjectDescriptor#English",
                        "$.schoolTypeDescriptor"
                    ),
                    CreateDescriptorReference(
                        malformedUriReferentialId,
                        "not-a-standard-descriptor-uri",
                        "$.programs[0].schoolTypeDescriptor"
                    ),
                ]
            )
        );

        result.SuccessfulDescriptorReferencesByPath.Should().BeEmpty();
        result
            .InvalidDescriptorReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.schoolTypeDescriptor", DescriptorReferenceFailureReason.Missing),
                ("$.programs[0].schoolTypeDescriptor", DescriptorReferenceFailureReason.Missing)
            );
    }

    [Test]
    public async Task It_uses_the_matched_document_resource_key_for_alias_rows_while_preserving_lookup_metadata()
    {
        var aliasReferentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingReferenceResolverAdapter([
            [
                new ReferenceLookupResult(
                    ReferentialId: aliasReferentialId,
                    DocumentId: 202,
                    ResourceKeyId: 11,
                    ReferentialIdentityResourceKeyId: 30,
                    IsDescriptor: false,
                    VerificationIdentityKey: "$$.schoolId=255901"
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
                    CreateDocumentReference(aliasReferentialId, "$.educationOrganizationReference"),
                ],
                DescriptorReferences: []
            )
        );

        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.educationOrganizationReference")]
            .ResourceKeyId.Should()
            .Be(11);
        result
            .LookupsByReferentialId[aliasReferentialId]
            .Result!.ReferentialIdentityResourceKeyId.Should()
            .Be(30);
    }

    [Test]
    public async Task It_fails_closed_when_a_same_type_document_lookup_resolves_to_the_wrong_natural_key()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingReferenceResolverAdapter([
            [
                new ReferenceLookupResult(
                    ReferentialId: referentialId,
                    DocumentId: 505,
                    ResourceKeyId: 11,
                    ReferentialIdentityResourceKeyId: 11,
                    IsDescriptor: false,
                    VerificationIdentityKey: "$$.schoolId=255902"
                ),
            ],
        ]);
        var sut = new ReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                new ReferenceResolverRequest(
                    MappingSet: CreateMappingSet(),
                    RequestResource: _requestResource,
                    DocumentReferences: [CreateDocumentReference(referentialId, "$.schoolReference")],
                    DescriptorReferences: []
                )
            );

        var exception = await act.Should().ThrowAsync<ReferenceLookupCorruptionException>();
        exception.Which.Message.Should().Contain("Reference lookup corruption detected");
        exception.Which.Message.Should().Contain(referentialId.Value.ToString());
        exception.Which.Message.Should().Contain("$.schoolReference");
        exception.Which.Message.Should().Contain("$$.schoolId=255901");
        exception.Which.Message.Should().Contain("$$.schoolId=255902");
    }

    [Test]
    public async Task It_fails_closed_when_a_wrong_type_document_lookup_returns_a_mismatched_verification_key()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingReferenceResolverAdapter([
            [
                new ReferenceLookupResult(
                    ReferentialId: referentialId,
                    DocumentId: 202,
                    ResourceKeyId: 12,
                    ReferentialIdentityResourceKeyId: 12,
                    IsDescriptor: false,
                    VerificationIdentityKey: "$$.localEducationAgencyId=255901"
                ),
            ],
        ]);
        var sut = new ReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                new ReferenceResolverRequest(
                    MappingSet: CreateMappingSet(),
                    RequestResource: _requestResource,
                    DocumentReferences: [CreateDocumentReference(referentialId, "$.schoolReference")],
                    DescriptorReferences: []
                )
            );

        var exception = await act.Should().ThrowAsync<ReferenceLookupCorruptionException>();
        exception.Which.Message.Should().Contain("Reference lookup corruption detected");
        exception.Which.Message.Should().Contain(referentialId.Value.ToString());
        exception.Which.Message.Should().Contain("$.schoolReference");
        exception.Which.Message.Should().Contain("$$.schoolId=255901");
        exception.Which.Message.Should().Contain("$$.localEducationAgencyId=255901");
    }

    [Test]
    public async Task It_fails_closed_when_a_same_type_descriptor_lookup_resolves_to_the_wrong_uri()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingReferenceResolverAdapter([
            [
                new ReferenceLookupResult(
                    ReferentialId: referentialId,
                    DocumentId: 606,
                    ResourceKeyId: 13,
                    ReferentialIdentityResourceKeyId: 13,
                    IsDescriptor: true,
                    VerificationIdentityKey: "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#wrong"
                ),
            ],
        ]);
        var sut = new ReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                new ReferenceResolverRequest(
                    MappingSet: CreateMappingSet(),
                    RequestResource: _requestResource,
                    DocumentReferences: [],
                    DescriptorReferences:
                    [
                        CreateDescriptorReference(
                            referentialId,
                            "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
                            "$.schoolTypeDescriptor"
                        ),
                    ]
                )
            );

        var exception = await act.Should().ThrowAsync<ReferenceLookupCorruptionException>();
        exception.Which.Message.Should().Contain("Reference lookup corruption detected");
        exception.Which.Message.Should().Contain(referentialId.Value.ToString());
        exception.Which.Message.Should().Contain("$.schoolTypeDescriptor");
        exception
            .Which.Message.Should()
            .Contain("$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative");
        exception.Which.Message.Should().Contain("$$.descriptor=uri://ed-fi.org/schooltypedescriptor#wrong");
    }

    [Test]
    public async Task It_fails_closed_when_a_wrong_type_descriptor_lookup_returns_a_mismatched_verification_key()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingReferenceResolverAdapter([
            [
                new ReferenceLookupResult(
                    ReferentialId: referentialId,
                    DocumentId: 404,
                    ResourceKeyId: 14,
                    ReferentialIdentityResourceKeyId: 14,
                    IsDescriptor: true,
                    VerificationIdentityKey: "$$.descriptor=uri://ed-fi.org/academicsubjectdescriptor#english"
                ),
            ],
        ]);
        var sut = new ReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                new ReferenceResolverRequest(
                    MappingSet: CreateMappingSet(),
                    RequestResource: _requestResource,
                    DocumentReferences: [],
                    DescriptorReferences:
                    [
                        CreateDescriptorReference(
                            referentialId,
                            "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
                            "$.schoolTypeDescriptor"
                        ),
                    ]
                )
            );

        var exception = await act.Should().ThrowAsync<ReferenceLookupCorruptionException>();
        exception.Which.Message.Should().Contain("Reference lookup corruption detected");
        exception.Which.Message.Should().Contain(referentialId.Value.ToString());
        exception.Which.Message.Should().Contain("$.schoolTypeDescriptor");
        exception
            .Which.Message.Should()
            .Contain("$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative");
        exception
            .Which.Message.Should()
            .Contain("$$.descriptor=uri://ed-fi.org/academicsubjectdescriptor#english");
    }

    [Test]
    public async Task It_classifies_mixed_failures_without_collapsing_repeated_paths_sharing_a_deduped_key()
    {
        var successfulDocumentReferentialId = new ReferentialId(Guid.NewGuid());
        var missingDocumentReferentialId = new ReferentialId(Guid.NewGuid());
        var incompatibleDocumentReferentialId = new ReferentialId(Guid.NewGuid());
        var successfulDescriptorReferentialId = new ReferentialId(Guid.NewGuid());
        var missingDescriptorReferentialId = new ReferentialId(Guid.NewGuid());
        var wrongDescriptorTypeReferentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingReferenceResolverAdapter([
            [
                new ReferenceLookupResult(
                    ReferentialId: successfulDocumentReferentialId,
                    DocumentId: 101,
                    ResourceKeyId: 11,
                    ReferentialIdentityResourceKeyId: 11,
                    IsDescriptor: false,
                    VerificationIdentityKey: "$$.schoolId=255901"
                ),
                new ReferenceLookupResult(
                    ReferentialId: incompatibleDocumentReferentialId,
                    DocumentId: 202,
                    ResourceKeyId: 12,
                    ReferentialIdentityResourceKeyId: 12,
                    IsDescriptor: false,
                    VerificationIdentityKey: "$$.schoolId=255901"
                ),
                new ReferenceLookupResult(
                    ReferentialId: successfulDescriptorReferentialId,
                    DocumentId: 303,
                    ResourceKeyId: 13,
                    ReferentialIdentityResourceKeyId: 13,
                    IsDescriptor: true,
                    VerificationIdentityKey: "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative"
                ),
                new ReferenceLookupResult(
                    ReferentialId: wrongDescriptorTypeReferentialId,
                    DocumentId: 404,
                    ResourceKeyId: 14,
                    ReferentialIdentityResourceKeyId: 14,
                    IsDescriptor: true,
                    VerificationIdentityKey: "$$.descriptor=uri://ed-fi.org/academicsubjectdescriptor#english"
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
                    CreateDocumentReference(successfulDocumentReferentialId, "$.schoolReference"),
                    CreateDocumentReference(missingDocumentReferentialId, "$.sections[0].schoolReference"),
                    CreateDocumentReference(missingDocumentReferentialId, "$.sections[1].schoolReference"),
                    CreateDocumentReference(
                        incompatibleDocumentReferentialId,
                        "$.localEducationAgencyReference"
                    ),
                ],
                DescriptorReferences:
                [
                    CreateDescriptorReference(
                        successfulDescriptorReferentialId,
                        "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
                        "$.schoolTypeDescriptor"
                    ),
                    CreateDescriptorReference(
                        missingDescriptorReferentialId,
                        "uri://ed-fi.org/SchoolTypeDescriptor#Missing",
                        "$.programs[0].schoolTypeDescriptor"
                    ),
                    CreateDescriptorReference(
                        missingDescriptorReferentialId,
                        "uri://ed-fi.org/SchoolTypeDescriptor#Missing",
                        "$.programs[1].schoolTypeDescriptor"
                    ),
                    CreateDescriptorReference(
                        wrongDescriptorTypeReferentialId,
                        "uri://ed-fi.org/AcademicSubjectDescriptor#English",
                        "$.alternateSchoolTypeDescriptor"
                    ),
                ]
            )
        );

        adapter.Requests.Should().ContainSingle();
        adapter
            .Requests[0]
            .ReferentialIds.Should()
            .Equal(
                successfulDocumentReferentialId,
                missingDocumentReferentialId,
                incompatibleDocumentReferentialId,
                successfulDescriptorReferentialId,
                missingDescriptorReferentialId,
                wrongDescriptorTypeReferentialId
            );

        result.SuccessfulDocumentReferencesByPath.Keys.Should().Equal(new JsonPath("$.schoolReference"));
        result
            .SuccessfulDescriptorReferencesByPath.Keys.Should()
            .Equal(new JsonPath("$.schoolTypeDescriptor"));

        result
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.sections[0].schoolReference", DocumentReferenceFailureReason.Missing),
                ("$.sections[1].schoolReference", DocumentReferenceFailureReason.Missing),
                ("$.localEducationAgencyReference", DocumentReferenceFailureReason.IncompatibleTargetType)
            );

        result
            .InvalidDescriptorReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.programs[0].schoolTypeDescriptor", DescriptorReferenceFailureReason.Missing),
                ("$.programs[1].schoolTypeDescriptor", DescriptorReferenceFailureReason.Missing),
                ("$.alternateSchoolTypeDescriptor", DescriptorReferenceFailureReason.DescriptorTypeMismatch)
            );

        result.HasFailures.Should().BeTrue();
    }

    private static MappingSet CreateMappingSet()
    {
        const string EffectiveSchemaHash = "test-hash";

        var studentKey = new ResourceKeyEntry(1, _requestResource, "1.0", false);
        var schoolKey = new ResourceKeyEntry(11, _schoolResource, "1.0", false);
        var localEducationAgencyKey = new ResourceKeyEntry(12, _localEducationAgencyResource, "1.0", false);
        var schoolTypeDescriptorKey = new ResourceKeyEntry(13, _schoolTypeDescriptorResource, "1.0", false);
        var academicSubjectDescriptorKey = new ResourceKeyEntry(
            14,
            _academicSubjectDescriptorResource,
            "1.0",
            false
        );
        var meetingKey = new ResourceKeyEntry(15, _meetingResource, "1.0", false);
        var educationOrganizationKey = new ResourceKeyEntry(30, _educationOrganizationResource, "1.0", true);
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: EffectiveSchemaHash,
            ResourceKeyCount: 7,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder:
            [
                studentKey,
                schoolKey,
                localEducationAgencyKey,
                schoolTypeDescriptorKey,
                academicSubjectDescriptorKey,
                meetingKey,
                educationOrganizationKey,
            ]
        );

        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchema,
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder:
            [
                new ConcreteResourceModel(
                    studentKey,
                    ResourceStorageKind.RelationalTables,
                    CreateRelationalResourceModel(_requestResource, "Student")
                ),
                new ConcreteResourceModel(
                    schoolKey,
                    ResourceStorageKind.RelationalTables,
                    CreateRelationalResourceModel(_schoolResource, "School")
                ),
                new ConcreteResourceModel(
                    localEducationAgencyKey,
                    ResourceStorageKind.RelationalTables,
                    CreateRelationalResourceModel(_localEducationAgencyResource, "LocalEducationAgency")
                ),
                new ConcreteResourceModel(
                    schoolTypeDescriptorKey,
                    ResourceStorageKind.SharedDescriptorTable,
                    CreateRelationalResourceModel(
                        _schoolTypeDescriptorResource,
                        "Descriptor",
                        ResourceStorageKind.SharedDescriptorTable
                    )
                ),
                new ConcreteResourceModel(
                    academicSubjectDescriptorKey,
                    ResourceStorageKind.SharedDescriptorTable,
                    CreateRelationalResourceModel(
                        _academicSubjectDescriptorResource,
                        "Descriptor",
                        ResourceStorageKind.SharedDescriptorTable
                    )
                ),
                new ConcreteResourceModel(
                    meetingKey,
                    ResourceStorageKind.RelationalTables,
                    CreateRelationalResourceModel(_meetingResource, "Meeting")
                ),
            ],
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
            ResourceKeyIdByResource: effectiveSchema.ResourceKeysInIdOrder.ToDictionary(
                entry => entry.Resource,
                entry => entry.ResourceKeyId
            ),
            ResourceKeyById: effectiveSchema.ResourceKeysInIdOrder.ToDictionary(
                entry => entry.ResourceKeyId,
                entry => entry
            ),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static RelationalResourceModel CreateRelationalResourceModel(
        QualifiedResourceName resource,
        string tableName,
        ResourceStorageKind storageKind = ResourceStorageKind.RelationalTables
    )
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), tableName),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: storageKind,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
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

    private static DocumentReference CreateMeetingDocumentReference(
        ReferentialId referentialId,
        string path,
        string meetingDateTime
    ) =>
        new(
            ResourceInfo: new BaseResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("Meeting"),
                IsDescriptor: false
            ),
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.meetingDateTime"), meetingDateTime),
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

    private sealed class SequencedReferenceResolverAdapter(
        IReadOnlyList<
            Func<ReferenceLookupRequest, CancellationToken, Task<IReadOnlyList<ReferenceLookupResult>>>
        > behaviors
    ) : IReferenceResolverAdapter
    {
        private readonly Queue<
            Func<ReferenceLookupRequest, CancellationToken, Task<IReadOnlyList<ReferenceLookupResult>>>
        > _behaviors = new(behaviors);

        public List<ReferenceLookupRequest> Requests { get; } = [];

        public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
            ReferenceLookupRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);

            if (!_behaviors.TryDequeue(out var behavior))
            {
                throw new AssertionException(
                    "No fake adapter behavior was configured for this resolver call."
                );
            }

            return behavior(request, cancellationToken);
        }
    }
}

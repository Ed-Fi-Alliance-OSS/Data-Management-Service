// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Semantics of the natural-key resolver, held constant against the behaviors
/// <c>Given_ReferenceResolver</c> pins for the hash-based resolver it replaces.
/// </summary>
[TestFixture]
public class Given_NaturalKeyReferenceResolver
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
    private static readonly QualifiedResourceName _programResource = new("Ed-Fi", "Program");
    private static readonly QualifiedResourceName _schoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );
    private static readonly QualifiedResourceName _academicSubjectDescriptorResource = new(
        "Ed-Fi",
        "AcademicSubjectDescriptor"
    );

    [Test]
    public async Task It_deduplicates_lookups_by_identity_value_within_a_single_request()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [Hit(0, 1, 101)],
            [],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            CreateRequest(
                documentReferences:
                [
                    CreateSchoolReference("$.schoolReference"),
                    CreateSchoolReference("$.sections[0].schoolReference"),
                    CreateSchoolReference("$.sections[1].schoolReference"),
                ]
            )
        );

        adapter.Batches.Should().ContainSingle();
        adapter.Batches[0].Groups.Should().ContainSingle();
        adapter
            .Batches[0]
            .Groups[0]
            .Entries.Select(entry => (entry.Ordinal, entry.Values[0]))
            .Should()
            .Equal([(1, (object)255901L)], because: "three occurrences of one identity are one probe entry");

        result
            .SuccessfulDocumentReferencesByPath.Keys.Select(path => path.Value)
            .Should()
            .Equal("$.schoolReference", "$.sections[0].schoolReference", "$.sections[1].schoolReference");
    }

    [Test]
    public async Task It_deduplicates_lookups_that_hash_differently_but_name_the_same_target_row()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [Hit(0, 1, 101)],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            CreateRequest(
                documentReferences:
                [
                    CreateSchoolReference("$.schoolReference"),
                    CreateSchoolReference("$.otherSchoolReference", new ReferentialId(Guid.NewGuid())),
                ]
            )
        );

        adapter.Batches[0].Groups[0].Entries.Should().ContainSingle();
        result.LookupsByReferentialId.Should().HaveCount(2, "the hash-keyed public map still lists both");
        result
            .LookupsByReferentialId.Values.Select(snapshot => snapshot.Result!.DocumentId)
            .Should()
            .AllBeEquivalentTo(101L);
    }

    [Test]
    public async Task It_memoizes_lookups_across_calls_within_the_same_request_scope()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [Hit(0, 1, 101)],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        _ = await sut.ResolveAsync(
            CreateRequest(documentReferences: [CreateSchoolReference("$.schoolReference")])
        );
        var second = await sut.ResolveAsync(
            CreateRequest(documentReferences: [CreateSchoolReference("$.sections[0].schoolReference")])
        );

        adapter.Batches.Should().ContainSingle("the second call is served entirely from the memo");
        second
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.sections[0].schoolReference")]
            .DocumentId.Should()
            .Be(101L);
    }

    [Test]
    public async Task It_reuses_a_memoized_miss_only_after_the_lookup_round_completes()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [],
            [],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var first = await sut.ResolveAsync(
            CreateRequest(
                documentReferences:
                [
                    CreateSchoolReference("$.schoolReference"),
                    CreateSchoolReference("$.sections[0].schoolReference"),
                ]
            )
        );

        adapter.Batches.Should().ContainSingle();
        adapter
            .Batches[0]
            .Groups[0]
            .Entries.Should()
            .ContainSingle("the second occurrence must not re-request within the same round");
        first.InvalidDocumentReferences.Should().HaveCount(2);

        var second = await sut.ResolveAsync(
            CreateRequest(documentReferences: [CreateSchoolReference("$.sections[1].schoolReference")])
        );

        adapter.Batches.Should().ContainSingle("the memoized miss is reused on the next call");
        second
            .InvalidDocumentReferences.Select(failure => failure.Reason)
            .Should()
            .Equal(DocumentReferenceFailureReason.Missing);
    }

    [Test]
    public async Task It_retries_the_same_lookup_after_an_adapter_exception_without_poisoning_request_scope()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter(
            [null, [Hit(0, 1, 101)]],
            new InvalidOperationException("transient backend failure")
        );
        var sut = new NaturalKeyReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                CreateRequest(documentReferences: [CreateSchoolReference("$.schoolReference")])
            );

        await act.Should().ThrowAsync<InvalidOperationException>();

        var retried = await sut.ResolveAsync(
            CreateRequest(documentReferences: [CreateSchoolReference("$.schoolReference")])
        );

        adapter.Batches.Should().HaveCount(2);
        retried
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.schoolReference")]
            .DocumentId.Should()
            .Be(101L);
    }

    [Test]
    public async Task It_retries_the_same_lookup_after_adapter_cancellation_without_poisoning_request_scope()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter(
            [null, [Hit(0, 1, 101)]],
            new OperationCanceledException()
        );
        var sut = new NaturalKeyReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                CreateRequest(documentReferences: [CreateSchoolReference("$.schoolReference")])
            );

        await act.Should().ThrowAsync<OperationCanceledException>();

        var retried = await sut.ResolveAsync(
            CreateRequest(documentReferences: [CreateSchoolReference("$.schoolReference")])
        );

        adapter.Batches.Should().HaveCount(2);
        retried.SuccessfulDocumentReferencesByPath.Should().ContainSingle();
    }

    [Test]
    public async Task It_maps_result_rows_back_by_ordinal_rather_than_row_order()
    {
        // Rows arrive in unspecified order on both dialects; only the projected ordinal attributes them.
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [Hit(0, 3, 303), Hit(0, 1, 101)],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            CreateRequest(
                documentReferences:
                [
                    CreateSchoolReference("$.schools[0].schoolReference", schoolId: 255901),
                    CreateSchoolReference("$.schools[1].schoolReference", schoolId: 255902),
                    CreateSchoolReference("$.schools[2].schoolReference", schoolId: 255903),
                ]
            )
        );

        result
            .SuccessfulDocumentReferencesByPath.Select(entry => (entry.Key.Value, entry.Value.DocumentId))
            .Should()
            .BeEquivalentTo(
                new[] { ("$.schools[0].schoolReference", 101L), ("$.schools[2].schoolReference", 303L) }
            );
        result
            .InvalidDocumentReferences.Select(failure => failure.Path.Value)
            .Should()
            .Equal("$.schools[1].schoolReference");
    }

    [Test]
    public async Task It_groups_document_references_before_descriptor_references_by_target()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [Hit(0, 1, 101), Hit(1, 1, 202), DescriptorHit(2, 1, 303, "SchoolTypeDescriptor", 13)],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        _ = await sut.ResolveAsync(
            CreateRequest(
                documentReferences:
                [
                    CreateSchoolReference("$.schoolReference"),
                    CreateLocalEducationAgencyReference("$.localEducationAgencyReference"),
                    CreateSchoolReference("$.sections[0].schoolReference", schoolId: 255902),
                ],
                descriptorReferences: [CreateSchoolTypeDescriptorReference("$.schoolTypeDescriptor")]
            )
        );

        var batch = adapter.Batches.Should().ContainSingle().Subject;
        batch
            .Groups.Select(group => (group.Target.ResourceName, group.GetType().Name, group.Entries.Count))
            .Should()
            .Equal(
                ("School", nameof(NaturalKeyProbeLookupGroup), 2),
                ("LocalEducationAgency", nameof(NaturalKeyProbeLookupGroup), 1),
                ("SchoolTypeDescriptor", nameof(DescriptorLookupGroup), 1)
            );
        batch
            .Groups[0]
            .Entries.Select(entry => entry.Ordinal)
            .Should()
            .Equal([1, 2], because: "ordinals are the one-based position within the group");
    }

    [Test]
    public async Task It_uses_the_matched_document_resource_key_for_alias_rows_while_preserving_lookup_metadata()
    {
        var aliasReferentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [AbstractHit(0, 1, 202, "Ed-Fi:School")],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            CreateRequest(
                documentReferences:
                [
                    CreateEducationOrganizationReference(
                        "$.educationOrganizationReference",
                        aliasReferentialId
                    ),
                ]
            )
        );

        adapter
            .Batches[0]
            .Groups[0]
            .Should()
            .BeOfType<NaturalKeyProbeLookupGroup>()
            .Which.Probe.ProbeTable.Name.Should()
            .Be("EducationOrganizationIdentity");
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.educationOrganizationReference")]
            .ResourceKeyId.Should()
            .Be(11, because: "the abstract discriminator names the matched concrete subtype");
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.educationOrganizationReference")]
            .DocumentId.Should()
            .Be(202L);
        result
            .LookupsByReferentialId[aliasReferentialId]
            .Result!.RequestedTargetResourceKeyId.Should()
            .Be(30, because: "the requested abstract target's key id is what the alias row carried");
    }

    [Test]
    public async Task It_rejects_an_abstract_discriminator_the_mapping_set_does_not_name()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [AbstractHit(0, 1, 202, "Ed-Fi:Unknown")],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                CreateRequest(
                    documentReferences:
                    [
                        CreateEducationOrganizationReference("$.educationOrganizationReference"),
                    ]
                )
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Ed-Fi:Unknown");
    }

    [Test]
    public async Task It_preserves_per_occurrence_diagnostics_while_materializing_success_maps()
    {
        var missingReferentialId = new ReferentialId(Guid.NewGuid());
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [Hit(0, 1, 101)],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            CreateRequest(
                documentReferences:
                [
                    CreateSchoolReference("$.schoolReference"),
                    CreateSchoolReference(
                        "$.sections[0].schoolReference",
                        missingReferentialId,
                        schoolId: 255902
                    ),
                    CreateSchoolReference(
                        "$.sections[1].schoolReference",
                        missingReferentialId,
                        schoolId: 255902
                    ),
                ]
            )
        );

        result.DocumentReferenceOccurrences.Should().HaveCount(3);
        result
            .DocumentReferenceOccurrences[2]
            .Lookup.Should()
            .BeSameAs(
                result.DocumentReferenceOccurrences[1].Lookup,
                "occurrences sharing a referential id share one snapshot"
            );
        result
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.sections[0].schoolReference", DocumentReferenceFailureReason.Missing),
                ("$.sections[1].schoolReference", DocumentReferenceFailureReason.Missing)
            );
        result.SuccessfulDocumentReferencesByPath.Keys.Should().Equal(new JsonPath("$.schoolReference"));
    }

    [Test]
    public async Task It_treats_missing_descriptors_as_missing_even_when_the_uri_text_is_nonstandard_or_implies_another_type()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            CreateRequest(
                descriptorReferences:
                [
                    CreateSchoolTypeDescriptorReference("$.schoolTypeDescriptor", uri: "not-a-uri"),
                    CreateSchoolTypeDescriptorReference(
                        "$.programs[0].schoolTypeDescriptor",
                        uri: "uri://ed-fi.org/AcademicSubjectDescriptor#English"
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
    public async Task It_reports_a_descriptor_uri_that_resolves_to_another_type_as_a_descriptor_type_mismatch()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [DescriptorHit(0, 1, 404, "AcademicSubjectDescriptor", 14)],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            CreateRequest(
                descriptorReferences:
                [
                    CreateSchoolTypeDescriptorReference(
                        "$.alternateSchoolTypeDescriptor",
                        uri: "uri://ed-fi.org/AcademicSubjectDescriptor#English"
                    ),
                ]
            )
        );

        result
            .InvalidDescriptorReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.alternateSchoolTypeDescriptor", DescriptorReferenceFailureReason.DescriptorTypeMismatch)
            );
    }

    [Test]
    public async Task It_lower_cases_descriptor_uris_before_probing_the_persisted_lowered_column()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [DescriptorHit(0, 1, 303, "SchoolTypeDescriptor", 13)],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        _ = await sut.ResolveAsync(
            CreateRequest(
                descriptorReferences:
                [
                    CreateSchoolTypeDescriptorReference(
                        "$.schoolTypeDescriptor",
                        uri: "uri://ed-fi.org/SchoolTypeDescriptor#Alternative"
                    ),
                ]
            )
        );

        adapter
            .Batches[0]
            .Groups[0]
            .Entries[0]
            .Values[0]
            .Should()
            .Be("uri://ed-fi.org/schooltypedescriptor#alternative");
    }

    [Test]
    public async Task It_lower_cases_descriptor_valued_identity_parts_before_probing()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [Hit(0, 1, 505)],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        _ = await sut.ResolveAsync(
            CreateRequest(documentReferences: [CreateProgramReference("$.programReference")])
        );

        adapter
            .Batches[0]
            .Groups[0]
            .Entries[0]
            .Values.Should()
            .Equal(255901L, "Gifted", "uri://ed-fi.org/schooltypedescriptor#alternative");
    }

    [Test]
    public async Task It_classifies_mixed_failures_without_collapsing_repeated_paths_sharing_a_deduped_key()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [
                Hit(0, 1, 101),
                DescriptorHit(1, 1, 303, "SchoolTypeDescriptor", 13),
                DescriptorHit(1, 3, 404, "AcademicSubjectDescriptor", 14),
            ],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var result = await sut.ResolveAsync(
            CreateRequest(
                documentReferences:
                [
                    CreateSchoolReference("$.schoolReference"),
                    CreateSchoolReference("$.sections[0].schoolReference", schoolId: 255902),
                    CreateSchoolReference("$.sections[1].schoolReference", schoolId: 255902),
                ],
                descriptorReferences:
                [
                    CreateSchoolTypeDescriptorReference("$.schoolTypeDescriptor"),
                    CreateSchoolTypeDescriptorReference(
                        "$.programs[0].schoolTypeDescriptor",
                        uri: "uri://ed-fi.org/SchoolTypeDescriptor#Missing"
                    ),
                    CreateSchoolTypeDescriptorReference(
                        "$.alternateSchoolTypeDescriptor",
                        uri: "uri://ed-fi.org/AcademicSubjectDescriptor#English"
                    ),
                ]
            )
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
                ("$.sections[1].schoolReference", DocumentReferenceFailureReason.Missing)
            );
        result
            .InvalidDescriptorReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.programs[0].schoolTypeDescriptor", DescriptorReferenceFailureReason.Missing),
                ("$.alternateSchoolTypeDescriptor", DescriptorReferenceFailureReason.DescriptorTypeMismatch)
            );
        result.HasFailures.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_an_unparseable_identity_value_as_a_missing_reference_with_a_logged_reason()
    {
        var logger = new RecordingLogger<NaturalKeyReferenceResolver>();
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter, logger);

        var result = await sut.ResolveAsync(
            CreateRequest(
                documentReferences:
                [
                    CreateMeetingReference("$.meetingReference", meetingDateTime: "not-a-timestamp"),
                ]
            )
        );

        adapter.Batches.Should().BeEmpty("an untypeable identity value never reaches the database");
        result
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(("$.meetingReference", DocumentReferenceFailureReason.Missing));
        result
            .LookupsByReferentialId.Values.Should()
            .AllSatisfy(snapshot => snapshot.Result.Should().BeNull());

        var record = logger.Records.Should().ContainSingle().Subject;
        record.Level.Should().Be(LogLevel.Warning);
        record.Message.Should().Contain("$.meetingReference");
        record.Message.Should().Contain("$.meetingDateTime");
        record.Message.Should().Contain("DateTime");
        record.Message.Should().Contain("MeetingDateTime");
    }

    [Test]
    public async Task It_rejects_a_request_identity_shape_that_the_compiled_probe_does_not_bind()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                CreateRequest(
                    documentReferences:
                    [
                        CreateDocumentReference(
                            _schoolResource,
                            [(new JsonPath("$.schoolNumber"), "255901")],
                            new ReferentialId(Guid.NewGuid()),
                            "$.schoolReference"
                        ),
                    ]
                )
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Ed-Fi.School");
        exception.Which.Message.Should().Contain("$.schoolId");
        exception.Which.Message.Should().Contain("$.schoolNumber");
    }

    [Test]
    public async Task It_rejects_two_lookups_for_one_target_that_order_their_identity_paths_differently()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                CreateRequest(
                    documentReferences:
                    [
                        CreateProgramReference("$.programReference"),
                        CreateDocumentReference(
                            _programResource,
                            [
                                (new JsonPath("$.programName"), "Gifted"),
                                (new JsonPath("$.educationOrganizationId"), "255901"),
                                (
                                    new JsonPath("$.programTypeDescriptor"),
                                    "uri://ed-fi.org/SchoolTypeDescriptor#Alternative"
                                ),
                            ],
                            new ReferentialId(Guid.NewGuid()),
                            "$.otherProgramReference"
                        ),
                    ]
                )
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("different identity path orderings");
    }

    [Test]
    public async Task It_rejects_a_target_the_mapping_set_never_compiled_a_probe_for()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                CreateRequest(
                    documentReferences:
                    [
                        CreateDocumentReference(
                            new QualifiedResourceName("Ed-Fi", "Uncompiled"),
                            [(new JsonPath("$.uncompiledId"), "1")],
                            new ReferentialId(Guid.NewGuid()),
                            "$.uncompiledReference"
                        ),
                    ]
                )
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("missing a compiled natural-key probe target");
        exception.Which.Message.Should().Contain("Ed-Fi.Uncompiled");
    }

    [Test]
    public async Task It_rejects_a_descriptor_target_without_a_compiled_discriminator_literal()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                CreateRequest(
                    descriptorReferences:
                    [
                        CreateDescriptorReference(
                            new QualifiedResourceName("Ed-Fi", "UncompiledDescriptor"),
                            new ReferentialId(Guid.NewGuid()),
                            "uri://ed-fi.org/UncompiledDescriptor#Value",
                            "$.uncompiledDescriptor"
                        ),
                    ]
                )
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception
            .Which.Message.Should()
            .Contain("missing a compiled descriptor discriminator literal for resource");
    }

    [Test]
    public async Task It_rejects_a_repeated_reference_path_within_one_request()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [Hit(0, 1, 101)],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        var act = async () =>
            await sut.ResolveAsync(
                CreateRequest(
                    documentReferences:
                    [
                        CreateSchoolReference("$.schoolReference"),
                        CreateSchoolReference("$.schoolReference"),
                    ]
                )
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("was extracted more than once within the same request");
    }

    [Test]
    public async Task It_types_identity_values_through_the_shared_relational_scalar_literal_parser()
    {
        var adapter = new RecordingNaturalKeyLookupAdapter([
            [Hit(0, 1, 909)],
        ]);
        var sut = new NaturalKeyReferenceResolver(adapter);

        _ = await sut.ResolveAsync(
            CreateRequest(documentReferences: [CreateMeetingReference("$.meetingReference")])
        );

        adapter
            .Batches[0]
            .Groups[0]
            .Entries[0]
            .Values[0]
            .Should()
            .Be(new DateTime(2024, 3, 5, 13, 45, 30, DateTimeKind.Utc));
    }

    // ── Fakes and fixture data ───────────────────────────────────────────────────────────────────

    private sealed class RecordingNaturalKeyLookupAdapter(
        IReadOnlyList<IReadOnlyList<NaturalKeyLookupRow>?> responses,
        Exception? throwOnNullResponse = null
    ) : INaturalKeyLookupAdapter
    {
        private readonly Queue<IReadOnlyList<NaturalKeyLookupRow>?> _responses = new(responses);
        private readonly List<NaturalKeyLookupBatch> _batches = [];

        public IReadOnlyList<NaturalKeyLookupBatch> Batches => _batches;

        public Task<IReadOnlyList<NaturalKeyLookupRow>> ResolveAsync(
            NaturalKeyLookupBatch batch,
            CancellationToken cancellationToken = default
        )
        {
            _batches.Add(batch);

            if (!_responses.TryDequeue(out var response))
            {
                throw new AssertionException(
                    "No fake adapter response was configured for this resolver call."
                );
            }

            if (response is null)
            {
                throw throwOnNullResponse
                    ?? new AssertionException("A throwing response was configured without an exception.");
            }

            return Task.FromResult(response);
        }
    }

    private static NaturalKeyLookupRow Hit(int groupIndex, int ordinal, long documentId) =>
        new(groupIndex, ordinal, documentId, null, null);

    private static NaturalKeyLookupRow AbstractHit(
        int groupIndex,
        int ordinal,
        long documentId,
        string discriminator
    ) => new(groupIndex, ordinal, documentId, discriminator, null);

    private static NaturalKeyLookupRow DescriptorHit(
        int groupIndex,
        int ordinal,
        long documentId,
        string discriminator,
        short resourceKeyId
    ) => new(groupIndex, ordinal, documentId, discriminator, resourceKeyId);

    private static ReferenceResolverRequest CreateRequest(
        IReadOnlyList<DocumentReference>? documentReferences = null,
        IReadOnlyList<DescriptorReference>? descriptorReferences = null
    ) =>
        new(
            MappingSet: CreateMappingSet(),
            RequestResource: _requestResource,
            DocumentReferences: documentReferences ?? [],
            DescriptorReferences: descriptorReferences ?? []
        );

    private static DocumentReference CreateSchoolReference(
        string path,
        ReferentialId? referentialId = null,
        long schoolId = 255901
    ) =>
        CreateDocumentReference(
            _schoolResource,
            [(new JsonPath("$.schoolId"), schoolId.ToString(CultureInfo.InvariantCulture))],
            referentialId
                ?? new ReferentialId(Guid.Parse($"11111111-0000-0000-0000-{schoolId:000000000000}")),
            path
        );

    private static DocumentReference CreateLocalEducationAgencyReference(
        string path,
        long localEducationAgencyId = 255901
    ) =>
        CreateDocumentReference(
            _localEducationAgencyResource,
            [
                (
                    new JsonPath("$.localEducationAgencyId"),
                    localEducationAgencyId.ToString(CultureInfo.InvariantCulture)
                ),
            ],
            new ReferentialId(Guid.Parse($"22222222-0000-0000-0000-{localEducationAgencyId:000000000000}")),
            path
        );

    private static DocumentReference CreateEducationOrganizationReference(
        string path,
        ReferentialId? referentialId = null
    ) =>
        CreateDocumentReference(
            _educationOrganizationResource,
            [(new JsonPath("$.educationOrganizationId"), "255901")],
            referentialId ?? new ReferentialId(Guid.Parse("33333333-0000-0000-0000-000000255901")),
            path
        );

    private static DocumentReference CreateMeetingReference(
        string path,
        string meetingDateTime = "2024-03-05T13:45:30Z"
    ) =>
        CreateDocumentReference(
            _meetingResource,
            [(new JsonPath("$.meetingDateTime"), meetingDateTime)],
            new ReferentialId(Guid.Parse("44444444-0000-0000-0000-000000000001")),
            path
        );

    private static DocumentReference CreateProgramReference(string path) =>
        CreateDocumentReference(
            _programResource,
            [
                (new JsonPath("$.educationOrganizationId"), "255901"),
                (new JsonPath("$.programName"), "Gifted"),
                (new JsonPath("$.programTypeDescriptor"), "uri://ed-fi.org/SchoolTypeDescriptor#Alternative"),
            ],
            new ReferentialId(Guid.Parse("55555555-0000-0000-0000-000000000001")),
            path
        );

    private static DescriptorReference CreateSchoolTypeDescriptorReference(
        string path,
        string uri = "uri://ed-fi.org/SchoolTypeDescriptor#Alternative"
    ) =>
        CreateDescriptorReference(
            _schoolTypeDescriptorResource,
            new ReferentialId(Guid.Parse("66666666-0000-0000-0000-000000000001")),
            uri,
            path
        );

    private static DocumentReference CreateDocumentReference(
        QualifiedResourceName targetResource,
        IReadOnlyList<(JsonPath Path, string Value)> identityElements,
        ReferentialId referentialId,
        string path
    ) =>
        new(
            ResourceInfo: new BaseResourceInfo(
                new ProjectName(targetResource.ProjectName),
                new ResourceName(targetResource.ResourceName),
                IsDescriptor: false
            ),
            DocumentIdentity: new DocumentIdentity([
                .. identityElements.Select(element => new DocumentIdentityElement(
                    element.Path,
                    element.Value
                )),
            ]),
            ReferentialId: referentialId,
            Path: new JsonPath(path)
        );

    private static DescriptorReference CreateDescriptorReference(
        QualifiedResourceName targetResource,
        ReferentialId referentialId,
        string uri,
        string path
    ) =>
        new(
            ResourceInfo: new BaseResourceInfo(
                new ProjectName(targetResource.ProjectName),
                new ResourceName(targetResource.ResourceName),
                IsDescriptor: true
            ),
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri),
            ]),
            ReferentialId: referentialId,
            Path: new JsonPath(path)
        );

    private static MappingSet CreateMappingSet()
    {
        const string EffectiveSchemaHash = "natural-key-resolver-test-hash";

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
        var programKey = new ResourceKeyEntry(16, _programResource, "1.0", false);
        var educationOrganizationKey = new ResourceKeyEntry(30, _educationOrganizationResource, "1.0", true);

        ResourceKeyEntry[] resourceKeysInIdOrder =
        [
            studentKey,
            schoolKey,
            localEducationAgencyKey,
            schoolTypeDescriptorKey,
            academicSubjectDescriptorKey,
            meetingKey,
            programKey,
            educationOrganizationKey,
        ];

        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: EffectiveSchemaHash,
            ResourceKeyCount: checked((short)resourceKeysInIdOrder.Length),
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: resourceKeysInIdOrder
        );

        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchema,
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder:
            [
                CreateConcreteResource(studentKey, "Student"),
                CreateConcreteResource(schoolKey, "School"),
                CreateConcreteResource(localEducationAgencyKey, "LocalEducationAgency"),
                CreateConcreteResource(meetingKey, "Meeting"),
                CreateConcreteResource(programKey, "Program"),
                CreateConcreteResource(
                    schoolTypeDescriptorKey,
                    "Descriptor",
                    ResourceStorageKind.SharedDescriptorTable
                ),
                CreateConcreteResource(
                    academicSubjectDescriptorKey,
                    "Descriptor",
                    ResourceStorageKind.SharedDescriptorTable
                ),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder:
            [
                new AbstractUnionViewInfo(
                    educationOrganizationKey,
                    new DbTableName(new DbSchemaName("edfi"), "EducationOrganization_View"),
                    [],
                    [
                        new AbstractUnionViewArm(
                            schoolKey,
                            new DbTableName(new DbSchemaName("edfi"), "School"),
                            []
                        ),
                        new AbstractUnionViewArm(
                            localEducationAgencyKey,
                            new DbTableName(new DbSchemaName("edfi"), "LocalEducationAgency"),
                            []
                        ),
                    ]
                ),
            ],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey(EffectiveSchemaHash, SqlDialect.Pgsql, "v1"),
            Model: modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: resourceKeysInIdOrder.ToDictionary(
                entry => entry.Resource,
                entry => entry.ResourceKeyId
            ),
            ResourceKeyById: resourceKeysInIdOrder.ToDictionary(entry => entry.ResourceKeyId, entry => entry),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        )
        {
            NaturalKeyProbeTargets = CreateNaturalKeyProbeTargets(),
            DescriptorProbeTarget = CreateDescriptorProbeTarget(),
        };
    }

    private static IReadOnlyDictionary<
        QualifiedResourceName,
        NaturalKeyProbeTarget
    > CreateNaturalKeyProbeTargets() =>
        new Dictionary<QualifiedResourceName, NaturalKeyProbeTarget>
        {
            [_schoolResource] = new(
                new DbTableName(new DbSchemaName("edfi"), "School"),
                new DbColumnName("DocumentId"),
                IsAbstract: false,
                [
                    new NaturalKeyProbeColumn(
                        new DbColumnName("SchoolId"),
                        new JsonPathExpression("$.schoolId", []),
                        new RelationalScalarType(ScalarKind.Int64),
                        DescriptorResource: null
                    ),
                ]
            ),
            [_localEducationAgencyResource] = new(
                new DbTableName(new DbSchemaName("edfi"), "LocalEducationAgency"),
                new DbColumnName("DocumentId"),
                IsAbstract: false,
                [
                    new NaturalKeyProbeColumn(
                        new DbColumnName("LocalEducationAgencyId"),
                        new JsonPathExpression("$.localEducationAgencyId", []),
                        new RelationalScalarType(ScalarKind.Int64),
                        DescriptorResource: null
                    ),
                ]
            ),
            [_meetingResource] = new(
                new DbTableName(new DbSchemaName("edfi"), "Meeting"),
                new DbColumnName("DocumentId"),
                IsAbstract: false,
                [
                    new NaturalKeyProbeColumn(
                        new DbColumnName("MeetingDateTime"),
                        new JsonPathExpression("$.meetingDateTime", []),
                        new RelationalScalarType(ScalarKind.DateTime),
                        DescriptorResource: null
                    ),
                ]
            ),
            [_programResource] = new(
                new DbTableName(new DbSchemaName("edfi"), "Program"),
                new DbColumnName("DocumentId"),
                IsAbstract: false,
                [
                    new NaturalKeyProbeColumn(
                        new DbColumnName("EducationOrganization_EducationOrganizationId"),
                        new JsonPathExpression("$.educationOrganizationId", []),
                        new RelationalScalarType(ScalarKind.Int64),
                        DescriptorResource: null
                    ),
                    new NaturalKeyProbeColumn(
                        new DbColumnName("ProgramName"),
                        new JsonPathExpression("$.programName", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                        DescriptorResource: null
                    ),
                    new NaturalKeyProbeColumn(
                        new DbColumnName("ProgramTypeDescriptor_DescriptorId"),
                        new JsonPathExpression("$.programTypeDescriptor", []),
                        new RelationalScalarType(ScalarKind.Int64),
                        DescriptorResource: _schoolTypeDescriptorResource
                    ),
                ]
            ),
            [_educationOrganizationResource] = new(
                new DbTableName(new DbSchemaName("edfi"), "EducationOrganizationIdentity"),
                new DbColumnName("DocumentId"),
                IsAbstract: true,
                [
                    new NaturalKeyProbeColumn(
                        new DbColumnName("EducationOrganizationId"),
                        new JsonPathExpression("$.educationOrganizationId", []),
                        new RelationalScalarType(ScalarKind.Int64),
                        DescriptorResource: null
                    ),
                ]
            ),
        }.ToFrozenDictionary();

    private static DescriptorProbeTarget CreateDescriptorProbeTarget() =>
        new(
            new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            DescriptorProbeColumns.UriLowered,
            new DbColumnName("Discriminator"),
            new Dictionary<QualifiedResourceName, string>
            {
                [_schoolTypeDescriptorResource] = "SchoolTypeDescriptor",
                [_academicSubjectDescriptorResource] = "AcademicSubjectDescriptor",
            }.ToFrozenDictionary()
        );

    private static ConcreteResourceModel CreateConcreteResource(
        ResourceKeyEntry resourceKey,
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

        return new ConcreteResourceModel(
            resourceKey,
            storageKind,
            new RelationalResourceModel(
                Resource: resourceKey.Resource,
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: storageKind,
                Root: rootTable,
                TablesInDependencyOrder: [rootTable],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            )
        );
    }
}

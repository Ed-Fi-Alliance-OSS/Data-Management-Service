// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheReadResponseShaper")]
public class Given_DocumentCacheReadResponseShaper
{
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 4, 3, 14, 10, 11, TimeSpan.Zero);
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
    );
    private static readonly DocumentUuid SecondDocumentUuid = new(
        Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
    );
    private static readonly QualifiedResourceName Resource =
        DocumentCacheMaterializerCoherenceMappingSet.SchoolResource;
    private static readonly MappingSet MappingSet = DocumentCacheMaterializerCoherenceMappingSet.Create(
        SqlDialect.Pgsql
    );
    private static readonly short ResourceKeyId = MappingSet.ResourceKeyIdByResource[Resource];

    [Test]
    public void It_shapes_a_fresh_resource_get_from_cached_json()
    {
        var readMaterializer = new RecordingReadMaterializer(new ResourceLinksOptions { Enabled = true });
        var sut = CreateShaper(readMaterializer, linksEnabled: true);
        var candidate = Candidate();
        var hit = FreshHit(candidate, CachedDocumentJson(candidate));

        DocumentCacheReadLookupResult<GetResult> result = sut.ShapeGetById(
            CreateGetByIdRequest(candidate, ResponseContentCoding.Gzip),
            hit
        );

        var success = result.CachedResult.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(DocumentUuid);
        success.LastModifiedDate.Should().Be(LastModifiedAt.UtcDateTime);
        success.EdfiDoc["name"]!.GetValue<string>().Should().Be("Lincoln High");
        success.EdfiDoc["id"]!.GetValue<string>().Should().Be(DocumentUuid.Value.ToString());
        success.EdfiDoc["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-04-03T14:10:11Z");
        hit.StreamEtag.Should().Be("91-01234567.j._.l.i");
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().Be("91-01234567.j._.l.g");
        success.EdfiDoc["schoolReference"]!["link"].Should().NotBeNull();
        readMaterializer.StripAttempts.Should().Be(1);
    }

    [TestCaseSource(nameof(InvalidCachedDocumentJsonScenarios))]
    public void It_falls_back_when_cached_document_json_violates_cache_invariants(string documentJson)
    {
        var sut = CreateShaper();
        var candidate = Candidate();

        DocumentCacheReadLookupResult<GetResult> result = sut.ShapeGetById(
            CreateGetByIdRequest(candidate),
            FreshHit(candidate, documentJson)
        );

        result.CachedResult.Should().BeNull();
        result
            .FallbackReason.Should()
            .Be(DocumentCacheReadAccelerationFallbackReason.CacheLookupInvariantFailure);
        result.RawLookupOutcome.Should().Be(DocumentCacheReadLookupOutcome.DeterministicInvariantFailure);
        result.InvariantDiagnostic.Should().NotBeNull();
        result.InvariantDiagnostic!.Message.Should().Contain("response shaping");
    }

    [Test]
    public void It_falls_back_when_get_by_id_stream_etag_does_not_match_the_fixed_stream_representation()
    {
        var sut = CreateShaper();
        var candidate = Candidate();

        DocumentCacheReadLookupResult<GetResult> result = sut.ShapeGetById(
            CreateGetByIdRequest(candidate),
            FreshHit(candidate, CachedDocumentJson(candidate), streamEtag: "corrupted-stream-etag")
        );

        AssertStreamEtagMismatchFallback(result);
    }

    [Test]
    public void It_propagates_unexpected_exceptions_from_served_etag_composition()
    {
        var exception = new InvalidOperationException("served etag composition failed");
        var sut = CreateShaper(servedEtagComposer: new ThrowingServedEtagComposer(exception));
        var candidate = Candidate();

        Action act = () =>
            sut.ShapeGetById(
                CreateGetByIdRequest(candidate),
                FreshHit(candidate, CachedDocumentJson(candidate))
            );

        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(exception);
    }

    [Test]
    public void It_propagates_unexpected_exceptions_from_readable_profile_projection()
    {
        var exception = new InvalidOperationException("readable profile projection failed");
        var sut = CreateShaper(readableProfileProjector: new ThrowingReadableProfileProjector(exception));
        var candidate = Candidate();

        Action act = () =>
            sut.ShapeGetById(
                CreateGetByIdRequest(
                    candidate,
                    readableProfileProjectionContext: CreateReadableProfileProjectionContext()
                ),
                FreshHit(candidate, CachedDocumentJson(candidate))
            );

        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(exception);
    }

    [Test]
    public void It_propagates_unexpected_exceptions_from_resource_link_stripping()
    {
        var exception = new InvalidOperationException("resource link stripping failed");
        var sut = CreateShaper(readMaterializer: new ThrowingReadMaterializer(exception));
        var candidate = Candidate();

        Action act = () =>
            sut.ShapeGetById(
                CreateGetByIdRequest(candidate),
                FreshHit(candidate, CachedDocumentJson(candidate))
            );

        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(exception);
    }

    [Test]
    public void It_injects_the_served_etag_before_profile_projection_and_strips_links_after_projection()
    {
        var linksOptions = new ResourceLinksOptions { Enabled = false };
        var readMaterializer = new RecordingReadMaterializer(linksOptions);
        var profileProjector = new RecordingReadableProfileProjector();
        var sut = CreateShaper(
            readMaterializer,
            linksEnabled: false,
            readableProfileProjector: profileProjector
        );
        var candidate = Candidate();
        var projectionContext = CreateReadableProfileProjectionContext();
        string expectedEtag = new ServedEtagComposer().Compose(
            new ServedEtagContext(
                MappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                projectionContext.ProfileName,
                LinksEnabled: false,
                candidate.ContentVersion,
                ResponseContentCoding.Brotli
            )
        );

        DocumentCacheReadLookupResult<GetResult> result = sut.ShapeGetById(
            CreateGetByIdRequest(candidate, ResponseContentCoding.Brotli, projectionContext),
            FreshHit(candidate, CachedDocumentJson(candidate))
        );

        var success = result.CachedResult.Should().BeOfType<GetResult.GetSuccess>().Subject;
        profileProjector.InputEtag.Should().Be(expectedEtag);
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().Be(expectedEtag);
        success.EdfiDoc["schoolReference"]!["link"].Should().BeNull();
        readMaterializer.StripAttempts.Should().Be(1);
    }

    [Test]
    public void It_shapes_descriptor_gets_with_the_descriptor_no_link_etag_variant()
    {
        var sut = CreateShaper(linksEnabled: true);
        var candidate = Candidate(resourceKeyId: 1);
        string documentJson = DescriptorDocumentJson(candidate);

        DocumentCacheReadLookupResult<GetResult> result = sut.ShapeGetById(
            CreateGetByIdRequest(
                candidate,
                ResponseContentCoding.Identity,
                resourceKind: DocumentCacheReadAccelerationResourceKind.Descriptor
            ),
            FreshHit(
                candidate,
                documentJson,
                resourceKind: DocumentCacheReadAccelerationResourceKind.Descriptor
            )
        );

        var success = result.CachedResult.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.EdfiDoc["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().Be("91-01234567.j._.n.i");
    }

    [Test]
    public void It_shapes_a_fresh_query_page_in_candidate_order()
    {
        var sut = CreateShaper();
        var first = Candidate(documentId: 345, documentUuid: DocumentUuid);
        var second = Candidate(documentId: 346, documentUuid: SecondDocumentUuid, contentVersion: 92);
        var hitPage = DocumentCacheReadBatchLookupResult.FromDocuments([
            FreshHit(first, CachedDocumentJson(first, "Lincoln High")),
            FreshHit(second, CachedDocumentJson(second, "Washington High")),
        ]);

        DocumentCacheReadLookupResult<QueryResult> result = sut.ShapeQuery(
            CreateQueryRequest(
                new DocumentCacheReadAccelerationCandidatePage(
                    [first, second],
                    TotalCount: 7,
                    HighestSelectedDocumentId: 346
                )
            ),
            hitPage
        );

        var success = result.CachedResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(7);
        success.HighestSelectedDocumentId.Should().Be(346);
        success
            .EdfiDocs.Select(document => document!["name"]!.GetValue<string>())
            .Should()
            .Equal("Lincoln High", "Washington High");
    }

    [Test]
    public void It_falls_back_when_a_fresh_query_page_does_not_match_the_authorized_candidate_page()
    {
        var sut = CreateShaper();
        var authorized = Candidate(documentId: 345, documentUuid: DocumentUuid);
        var mismatchedHit = Candidate(documentId: 346, documentUuid: SecondDocumentUuid, contentVersion: 92);
        var hitPage = DocumentCacheReadBatchLookupResult.FromDocuments([
            FreshHit(mismatchedHit, CachedDocumentJson(mismatchedHit)),
        ]);

        DocumentCacheReadLookupResult<QueryResult> result = sut.ShapeQuery(
            CreateQueryRequest(
                new DocumentCacheReadAccelerationCandidatePage(
                    [authorized],
                    TotalCount: 1,
                    HighestSelectedDocumentId: 345
                )
            ),
            hitPage
        );

        result.CachedResult.Should().BeNull();
        result
            .FallbackReason.Should()
            .Be(DocumentCacheReadAccelerationFallbackReason.CacheLookupInvariantFailure);
        result.RawLookupOutcome.Should().Be(DocumentCacheReadLookupOutcome.DeterministicInvariantFailure);
        result.InvariantDiagnostic.Should().NotBeNull();
        result
            .InvariantDiagnostic!.Message.Should()
            .Contain(nameof(DocumentCacheReadResponseShapingFailureReason.QueryHitCandidateMismatch));
    }

    [Test]
    public void It_falls_back_without_mixing_when_a_query_page_contains_a_non_fresh_document()
    {
        var sut = CreateShaper();
        var first = Candidate(documentId: 345, documentUuid: DocumentUuid);
        var second = Candidate(documentId: 346, documentUuid: SecondDocumentUuid, contentVersion: 92);
        var hitPage = DocumentCacheReadBatchLookupResult.FromDocuments([
            FreshHit(first, CachedDocumentJson(first, "Lincoln High")),
            new DocumentCacheReadDocumentLookupResult.Fallback(
                DocumentCacheReadLookupOutcome.StaleCacheRow,
                second,
                "DocumentCache row is stale."
            ),
        ]);

        DocumentCacheReadLookupResult<QueryResult> result = sut.ShapeQuery(
            CreateQueryRequest(
                new DocumentCacheReadAccelerationCandidatePage(
                    [first, second],
                    TotalCount: 2,
                    HighestSelectedDocumentId: 346
                )
            ),
            hitPage
        );

        result.CachedResult.Should().BeNull();
        result.FallbackReason.Should().Be(DocumentCacheReadAccelerationFallbackReason.CacheLookupStale);
        result.RawLookupOutcome.Should().Be(DocumentCacheReadLookupOutcome.StaleCacheRow);
    }

    [Test]
    public void It_falls_back_without_mixing_when_a_query_page_has_a_mismatched_stream_etag()
    {
        var sut = CreateShaper();
        var first = Candidate(documentId: 345, documentUuid: DocumentUuid);
        var second = Candidate(documentId: 346, documentUuid: SecondDocumentUuid, contentVersion: 92);
        var hitPage = DocumentCacheReadBatchLookupResult.FromDocuments([
            FreshHit(first, CachedDocumentJson(first, "Lincoln High")),
            FreshHit(second, CachedDocumentJson(second, "Washington High"), streamEtag: "wrong-stream-etag"),
        ]);

        DocumentCacheReadLookupResult<QueryResult> result = sut.ShapeQuery(
            CreateQueryRequest(
                new DocumentCacheReadAccelerationCandidatePage(
                    [first, second],
                    TotalCount: 2,
                    HighestSelectedDocumentId: 346
                )
            ),
            hitPage
        );

        AssertStreamEtagMismatchFallback(result);
    }

    private static IEnumerable<TestCaseData> InvalidCachedDocumentJsonScenarios()
    {
        var candidate = Candidate();

        yield return new TestCaseData("{").SetName("Invalid JSON");
        yield return new TestCaseData("[]").SetName("Root array");
        yield return new TestCaseData(CachedDocumentJson(candidate, includeEtag: true)).SetName(
            "Stored etag"
        );
        yield return new TestCaseData(
            CachedDocumentJson(candidate with { DocumentUuid = SecondDocumentUuid })
        ).SetName("Id mismatch");
        yield return new TestCaseData(
            CachedDocumentJson(candidate with { ContentLastModifiedAt = LastModifiedAt.AddSeconds(1) })
        ).SetName("Last modified mismatch");
    }

    private static DocumentCacheReadResponseShaper CreateShaper(
        IRelationalReadMaterializer? readMaterializer = null,
        bool linksEnabled = true,
        IReadableProfileProjector? readableProfileProjector = null,
        IServedEtagComposer? servedEtagComposer = null
    ) =>
        new(
            readMaterializer
                ?? new RecordingReadMaterializer(new ResourceLinksOptions { Enabled = linksEnabled }),
            readableProfileProjector ?? new PassthroughReadableProfileProjector(),
            servedEtagComposer ?? new ServedEtagComposer(),
            Options.Create(new ResourceLinksOptions { Enabled = linksEnabled }),
            NullLogger<DocumentCacheReadResponseShaper>.Instance
        );

    private static DocumentCacheReadAccelerationGetByIdRequest CreateGetByIdRequest(
        DocumentCacheReadAccelerationCandidate candidate,
        ResponseContentCoding responseContentCoding = ResponseContentCoding.Identity,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null,
        DocumentCacheReadAccelerationResourceKind resourceKind =
            DocumentCacheReadAccelerationResourceKind.Resource
    ) =>
        new(
            "TenantA",
            MappingSet,
            Resource,
            candidate.DocumentUuid,
            RelationalGetRequestReadMode.ExternalResponse,
            resourceKind,
            DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
            (_, _) => Task.FromResult<GetResult>(new GetResult.GetFailureNotExists()),
            candidate
        )
        {
            ReadableProfileProjectionContext = readableProfileProjectionContext,
            ResponseContentCoding = responseContentCoding,
        };

    private static DocumentCacheReadAccelerationQueryRequest CreateQueryRequest(
        DocumentCacheReadAccelerationCandidatePage candidatePage
    ) =>
        new(
            "TenantA",
            MappingSet,
            Resource,
            DocumentCacheReadAccelerationResourceKind.Resource,
            DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
            (_, _) => Task.FromResult<QueryResult>(new QueryResult.QueryFailureKnownError("fallback")),
            candidatePage
        );

    private static DocumentCacheReadAccelerationCandidate Candidate(
        long documentId = 345,
        DocumentUuid? documentUuid = null,
        long contentVersion = 91,
        short? resourceKeyId = null
    ) =>
        new(
            documentId,
            documentUuid ?? DocumentUuid,
            resourceKeyId ?? ResourceKeyId,
            contentVersion,
            LastModifiedAt
        );

    private static DocumentCacheReadDocumentLookupResult.FreshHit FreshHit(
        DocumentCacheReadAccelerationCandidate candidate,
        string documentJson,
        string? streamEtag = null,
        DocumentCacheReadAccelerationResourceKind resourceKind =
            DocumentCacheReadAccelerationResourceKind.Resource
    ) =>
        new(
            candidate,
            documentJson,
            streamEtag ?? ComposeFixedStreamEtag(candidate, resourceKind),
            candidate.ContentLastModifiedAt
        );

    private static string ComposeFixedStreamEtag(
        DocumentCacheReadAccelerationCandidate candidate,
        DocumentCacheReadAccelerationResourceKind resourceKind
    ) =>
        resourceKind switch
        {
            DocumentCacheReadAccelerationResourceKind.Resource =>
                DocumentCacheMaterializerStreamEtagComposer.ComposeForResource(
                    new ServedEtagComposer(),
                    MappingSet,
                    candidate.ContentVersion
                ),
            DocumentCacheReadAccelerationResourceKind.Descriptor =>
                DocumentCacheMaterializerStreamEtagComposer.ComposeForDescriptor(
                    new ServedEtagComposer(),
                    MappingSet,
                    candidate.ContentVersion
                ),
            _ => throw new ArgumentOutOfRangeException(nameof(resourceKind), resourceKind, null),
        };

    private static void AssertStreamEtagMismatchFallback<TResult>(
        DocumentCacheReadLookupResult<TResult> result
    )
        where TResult : class
    {
        result.CachedResult.Should().BeNull();
        result
            .FallbackReason.Should()
            .Be(DocumentCacheReadAccelerationFallbackReason.CacheLookupInvariantFailure);
        result.RawLookupOutcome.Should().Be(DocumentCacheReadLookupOutcome.DeterministicInvariantFailure);
        result.InvariantDiagnostic.Should().NotBeNull();
        result.InvariantDiagnostic!.Message.Should().Contain("FixedStreamEtagMismatch");
        result.InvariantDiagnostic!.Message.Should().NotContain(DocumentUuid.Value.ToString());
        result.InvariantDiagnostic!.Message.Should().NotContain(SecondDocumentUuid.Value.ToString());
        result.InvariantDiagnostic!.Message.Should().NotContain("corrupted-stream-etag");
        result.InvariantDiagnostic!.Message.Should().NotContain("wrong-stream-etag");
    }

    private static string CachedDocumentJson(
        DocumentCacheReadAccelerationCandidate candidate,
        string name = "Lincoln High",
        bool includeEtag = false
    )
    {
        var document = new JsonObject
        {
            ["id"] = candidate.DocumentUuid.Value.ToString(),
            ["_lastModifiedDate"] = FormatLastModifiedDate(candidate.ContentLastModifiedAt),
            ["name"] = name,
            ["schoolReference"] = new JsonObject
            {
                ["schoolId"] = 255901,
                ["link"] = new JsonObject
                {
                    ["rel"] = "School",
                    ["href"] = "/ed-fi/schools/11112222-3333-4444-5555-666677778888",
                },
            },
        };

        if (includeEtag)
        {
            document["_etag"] = "cached-etag";
        }

        return document.ToJsonString();
    }

    private static string DescriptorDocumentJson(DocumentCacheReadAccelerationCandidate candidate) =>
        new JsonObject
        {
            ["id"] = candidate.DocumentUuid.Value.ToString(),
            ["_lastModifiedDate"] = FormatLastModifiedDate(candidate.ContentLastModifiedAt),
            ["namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
            ["codeValue"] = "Alternative",
            ["shortDescription"] = "Alternative",
        }.ToJsonString();

    private static string FormatLastModifiedDate(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static ReadableProfileProjectionContext CreateReadableProfileProjectionContext() =>
        new(
            new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("name"), new PropertyRule("schoolReference")],
                [],
                [],
                []
            ),
            new HashSet<string>(StringComparer.Ordinal) { "schoolReference" }
        )
        {
            ProfileName = "School-Profile",
        };

    private sealed class RecordingReadMaterializer(ResourceLinksOptions linksOptions)
        : IRelationalReadMaterializer
    {
        public int StripAttempts { get; private set; }

        public JsonNode Materialize(RelationalReadMaterializationRequest request) =>
            throw new InvalidOperationException(
                "Materialization is not used by cache response shaping tests."
            );

        public IReadOnlyList<MaterializedDocument> MaterializePage(
            RelationalReadPageMaterializationRequest request
        ) =>
            throw new InvalidOperationException(
                "Materialization is not used by cache response shaping tests."
            );

        public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan)
        {
            StripAttempts++;

            if (linksOptions.Enabled || document is not JsonObject documentObject)
            {
                return;
            }

            if (documentObject["schoolReference"] is JsonObject schoolReference)
            {
                schoolReference.Remove("link");
            }
        }
    }

    private sealed class ThrowingReadMaterializer(Exception exception) : IRelationalReadMaterializer
    {
        public JsonNode Materialize(RelationalReadMaterializationRequest request) =>
            throw new InvalidOperationException(
                "Materialization is not used by cache response shaping tests."
            );

        public IReadOnlyList<MaterializedDocument> MaterializePage(
            RelationalReadPageMaterializationRequest request
        ) =>
            throw new InvalidOperationException(
                "Materialization is not used by cache response shaping tests."
            );

        public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan) => throw exception;
    }

    private sealed class PassthroughReadableProfileProjector : IReadableProfileProjector
    {
        public JsonNode Project(
            JsonNode reconstitutedDocument,
            ContentTypeDefinition readContentType,
            IReadOnlySet<string> identityPropertyNames
        ) => reconstitutedDocument.DeepClone();
    }

    private sealed class RecordingReadableProfileProjector : IReadableProfileProjector
    {
        public string? InputEtag { get; private set; }

        public JsonNode Project(
            JsonNode reconstitutedDocument,
            ContentTypeDefinition readContentType,
            IReadOnlySet<string> identityPropertyNames
        )
        {
            InputEtag = reconstitutedDocument["_etag"]!.GetValue<string>();

            return new JsonObject
            {
                ["id"] = reconstitutedDocument["id"]!.DeepClone(),
                ["_etag"] = reconstitutedDocument["_etag"]!.DeepClone(),
                ["_lastModifiedDate"] = reconstitutedDocument["_lastModifiedDate"]!.DeepClone(),
                ["name"] = reconstitutedDocument["name"]!.DeepClone(),
                ["schoolReference"] = reconstitutedDocument["schoolReference"]!.DeepClone(),
            };
        }
    }

    private sealed class ThrowingReadableProfileProjector(Exception exception) : IReadableProfileProjector
    {
        public JsonNode Project(
            JsonNode reconstitutedDocument,
            ContentTypeDefinition readContentType,
            IReadOnlySet<string> identityPropertyNames
        ) => throw exception;
    }

    private sealed class ThrowingServedEtagComposer(Exception exception) : IServedEtagComposer
    {
        public string Compose(ServedEtagContext context) => throw exception;
    }
}

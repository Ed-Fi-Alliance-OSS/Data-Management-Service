// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheMaterializerContract
{
    private static readonly MappingSetKey MappingSetKey = new("test-hash", SqlDialect.Pgsql, "v1");

    [Test]
    public void It_materializes_by_internal_document_id_not_public_document_uuid_lookup()
    {
        var request = new DocumentCacheMaterializationRequest(
            CreateTargetContext(),
            documentId: 123,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.DurableWorkProjection,
            CancellationToken.None
        );

        request.DocumentId.Should().Be(123);
        typeof(DocumentCacheMaterializationRequest)
            .GetProperty(nameof(DocumentUuid))
            .Should()
            .BeNull("the materializer must not accept public DocumentUuid lookups");
    }

    [Test]
    public void It_requires_TargetContext_to_mark_effective_schema_and_resource_key_seed_validation()
    {
        var targetKey = new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7));
        var mappingSet = CreateMappingSet();

        var targetContext = new DocumentCacheMaterializationTargetContext(
            targetKey,
            mappingSet,
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
        );

        targetContext.TargetKey.Should().Be(targetKey);
        targetContext.MappingSet.Should().BeSameAs(mappingSet);
        targetContext
            .TargetValidation.Should()
            .Be(DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated);

        Action invalidValidation = () =>
            _ = new DocumentCacheMaterializationTargetContext(
                targetKey,
                mappingSet,
                (DocumentCacheMaterializationTargetValidation)0
            );
        invalidValidation
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("*EffectiveSchema*ResourceKey seed*");

        Action invalidDataStoreId = () =>
            _ = new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(0));
        invalidDataStoreId
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("*DataStoreId*positive*");
    }

    [Test]
    public void It_keeps_selected_required_content_version_as_request_local_diagnostics_only()
    {
        var candidate = CreateCandidate();
        var result = new DocumentCacheMaterializationResult.Success(candidate);

        typeof(DocumentCacheMaterializationRequest)
            .GetProperty(nameof(DocumentCacheMaterializationRequest.SelectedRequiredContentVersion))
            .Should()
            .NotBeNull();
        PublicPropertyNames(typeof(DocumentCacheMaterializationCandidate))
            .Should()
            .NotContain(nameof(DocumentCacheMaterializationRequest.SelectedRequiredContentVersion));
        PublicPropertyNames(result.GetType())
            .Should()
            .NotContain(nameof(DocumentCacheMaterializationRequest.SelectedRequiredContentVersion));
    }

    [Test]
    public void It_exposes_only_cache_candidate_fields_on_success()
    {
        var candidate = CreateCandidate();

        PublicPropertyNames(typeof(DocumentCacheMaterializationCandidate))
            .Should()
            .BeEquivalentTo(
                nameof(DocumentCacheMaterializationCandidate.DocumentId),
                nameof(DocumentCacheMaterializationCandidate.DocumentUuid),
                nameof(DocumentCacheMaterializationCandidate.ProjectName),
                nameof(DocumentCacheMaterializationCandidate.ResourceName),
                nameof(DocumentCacheMaterializationCandidate.ResourceVersion),
                nameof(DocumentCacheMaterializationCandidate.ContentVersion),
                nameof(DocumentCacheMaterializationCandidate.LastModifiedAt),
                nameof(DocumentCacheMaterializationCandidate.StreamEtag),
                nameof(DocumentCacheMaterializationCandidate.DocumentJson)
            );
        candidate.DocumentJson.Should().BeOfType<JsonObject>();
        candidate.StreamEtag.Should().Be("\"11-fixed-stream\"");
    }

    [Test]
    public void It_keeps_cache_write_lifecycle_authorization_and_computed_decisions_out_of_the_candidate()
    {
        PublicPropertyNames(typeof(DocumentCacheMaterializationCandidate))
            .Should()
            .NotContain(
                "ComputedAt",
                "ShouldWriteCache",
                "CacheWriteDecision",
                "CaughtUp",
                "AuthorizationDecision",
                "LifecycleState",
                "SelectedRequiredContentVersion"
            );
    }

    [Test]
    public void It_limits_ordinary_non_success_outcomes_to_missing_source_and_source_changed()
    {
        typeof(DocumentCacheMaterializationResult)
            .GetNestedTypes(BindingFlags.Public)
            .Where(type => type != typeof(DocumentCacheMaterializationResult.Success))
            .Select(type => type.Name)
            .Should()
            .BeEquivalentTo("MissingSource", "SourceChangedDuringHydration");

        DocumentCacheMaterializationResult
            .MissingSource.Instance.Should()
            .BeOfType<DocumentCacheMaterializationResult.MissingSource>();
        DocumentCacheMaterializationResult
            .SourceChangedDuringHydration.Instance.Should()
            .BeOfType<DocumentCacheMaterializationResult.SourceChangedDuringHydration>();
    }

    [Test]
    public void It_exposes_the_materializer_service_boundary_without_a_separate_cancellation_parameter()
    {
        MethodInfo materialize = typeof(IDocumentCacheMaterializer).GetMethod(
            nameof(IDocumentCacheMaterializer.MaterializeAsync)
        )!;

        materialize.ReturnType.Should().Be(typeof(Task<DocumentCacheMaterializationResult>));
        materialize
            .GetParameters()
            .Should()
            .ContainSingle(parameter =>
                parameter.ParameterType == typeof(DocumentCacheMaterializationRequest)
            );
        typeof(DocumentCacheMaterializationRequest)
            .GetProperty(nameof(DocumentCacheMaterializationRequest.CancellationToken))
            .Should()
            .NotBeNull();
    }

    [Test]
    public void It_uses_bounded_projection_processing_exceptions_for_invariant_failures()
    {
        var metadata = CreateFailureMetadata();
        var exception = new DocumentCacheProjectionProcessingException(
            DocumentCacheProjectionProcessingFailureReason.DocumentJsonContainsEtag,
            metadata
        );

        exception.Reason.Should().Be(DocumentCacheProjectionProcessingFailureReason.DocumentJsonContainsEtag);
        exception.FailureMetadata.Should().BeSameAs(metadata);
        exception
            .Message.Should()
            .Contain(nameof(DocumentCacheProjectionProcessingFailureReason.DocumentJsonContainsEtag));
        Enum.GetNames<DocumentCacheProjectionProcessingFailureReason>()
            .Should()
            .BeEquivalentTo(
                "StableSourceBodyMissing",
                "DocumentJsonNotObject",
                "DocumentJsonIdMismatch",
                "DocumentJsonLastModifiedDateMismatch",
                "DocumentJsonContainsEtag"
            );
    }

    [Test]
    public void It_uses_target_fatal_mapping_exceptions_instead_of_materialization_outcomes()
    {
        var metadata = CreateFailureMetadata() with { ResourceKeyId = 12 };
        var exception = new DocumentCacheTargetMappingException(
            DocumentCacheTargetMappingFailureReason.ReadPlanMissing,
            metadata
        );

        exception.Reason.Should().Be(DocumentCacheTargetMappingFailureReason.ReadPlanMissing);
        exception.FailureMetadata.Should().BeSameAs(metadata);
        typeof(DocumentCacheMaterializationResult)
            .GetNestedTypes(BindingFlags.Public)
            .Select(type => type.Name)
            .Should()
            .NotContain(name => name.Contains("Mapping", StringComparison.OrdinalIgnoreCase));
        Enum.GetNames<DocumentCacheTargetMappingFailureReason>()
            .Should()
            .BeEquivalentTo(
                "ResourceKeyMissingFromMappingSet",
                "ReadPlanMissing",
                "UnsupportedResourceStorageKind",
                "ConcreteResourceModelMissing",
                "ConcreteResourceModelMismatch",
                "ResourceKeyMetadataMismatch",
                "ReadPlanMetadataMismatch"
            );
    }

    [Test]
    public void It_sanitizes_diagnostic_keys_in_exception_messages()
    {
        var metadata = new DocumentCacheMaterializerFailureMetadata(
            new DocumentCacheProjectionTargetKey("tenant-a\r\nFORGED!", new DataStoreId(7)),
            new MappingSetKey("hash\r\nFORGED!", SqlDialect.Pgsql, "v1\r\nFORGED!"),
            DocumentCacheMaterializationPurpose.DurableWorkProjection,
            documentId: 123
        );

        var projectionException = new DocumentCacheProjectionProcessingException(
            DocumentCacheProjectionProcessingFailureReason.DocumentJsonContainsEtag,
            metadata
        );
        var mappingException = new DocumentCacheTargetMappingException(
            DocumentCacheTargetMappingFailureReason.ReadPlanMissing,
            metadata
        );

        projectionException
            .Message.Should()
            .Contain("tenant-aFORGED/7")
            .And.Contain("hashFORGED/Pgsql/v1FORGED")
            .And.NotContain("\r")
            .And.NotContain("\n")
            .And.NotContain("!");
        mappingException
            .Message.Should()
            .Contain("tenant-aFORGED/7")
            .And.Contain("hashFORGED/Pgsql/v1FORGED")
            .And.NotContain("\r")
            .And.NotContain("\n")
            .And.NotContain("!");
    }

    [Test]
    public void It_keeps_cache_work_lifecycle_authorization_profile_and_kafka_services_out_of_the_runtime_boundary()
    {
        var constructor = typeof(DocumentCacheMaterializer)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should()
            .ContainSingle()
            .Subject;
        var parameterTypes = constructor
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        parameterTypes
            .Should()
            .Equal(
                typeof(IDocumentCacheSourceMetadataReader),
                typeof(IDocumentCacheDescriptorHydrator),
                typeof(IDocumentCacheMaterializationDataStore),
                typeof(IRelationalReadMaterializer),
                typeof(IServedEtagComposer)
            );

        string[] forbiddenDependencyNameFragments =
        [
            "CacheWriter",
            "DocumentCacheRepository",
            "DocumentCacheState",
            "DocumentProjectionWork",
            "DurableWork",
            "Lifecycle",
            "Authorization",
            "ReadableProfile",
            "Kafka",
            "Envelope",
        ];

        parameterTypes
            .Select(type => type.Name)
            .Should()
            .NotContain(name =>
                forbiddenDependencyNameFragments.Any(fragment =>
                    name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                )
            );
    }

    [Test]
    public void It_carries_sanitized_failure_metadata_without_document_json_or_authorization_data()
    {
        PublicPropertyNames(typeof(DocumentCacheMaterializerFailureMetadata))
            .Should()
            .BeEquivalentTo(
                nameof(DocumentCacheMaterializerFailureMetadata.TargetKey),
                nameof(DocumentCacheMaterializerFailureMetadata.MappingSetKey),
                nameof(DocumentCacheMaterializerFailureMetadata.Purpose),
                nameof(DocumentCacheMaterializerFailureMetadata.DocumentId),
                nameof(DocumentCacheMaterializerFailureMetadata.SelectedRequiredContentVersion),
                nameof(DocumentCacheMaterializerFailureMetadata.ResourceKeyId),
                nameof(DocumentCacheMaterializerFailureMetadata.ProjectName),
                nameof(DocumentCacheMaterializerFailureMetadata.ResourceName),
                nameof(DocumentCacheMaterializerFailureMetadata.ResourceVersion)
            );
        PublicPropertyNames(typeof(DocumentCacheMaterializerFailureMetadata))
            .Should()
            .NotContain("DocumentJson", "AuthorizationContext", "AuthorizationDecision");
    }

    private static DocumentCacheMaterializationCandidate CreateCandidate() =>
        new(
            documentId: 123,
            documentUuid: new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            projectName: "Ed-Fi",
            resourceName: "School",
            resourceVersion: "5.3.0",
            contentVersion: 11,
            lastModifiedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            streamEtag: "\"11-fixed-stream\"",
            documentJson: JsonNode
                .Parse(
                    """
                    {"id":"11111111-1111-1111-1111-111111111111","_lastModifiedDate":"2026-01-01T00:00:00Z","nameOfInstitution":"Lincoln High"}
                    """
                )!
                .AsObject()
        );

    private static DocumentCacheMaterializerFailureMetadata CreateFailureMetadata() =>
        new(
            new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
            MappingSetKey,
            DocumentCacheMaterializationPurpose.DurableWorkProjection,
            documentId: 123
        )
        {
            SelectedRequiredContentVersion = 456,
            ProjectName = "Ed-Fi",
            ResourceName = "School",
            ResourceVersion = "5.3.0",
        };

    private static DocumentCacheMaterializationTargetContext CreateTargetContext() =>
        new(
            new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
            CreateMappingSet(),
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
        );

    private static MappingSet CreateMappingSet() =>
        new(
            MappingSetKey,
            Model: null!,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );

    private static string[] PublicPropertyNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .ToArray();
}

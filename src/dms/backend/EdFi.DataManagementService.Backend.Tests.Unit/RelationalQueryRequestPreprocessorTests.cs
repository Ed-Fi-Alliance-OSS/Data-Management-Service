// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalQueryRequestPreprocessor
{
    [Test]
    public async Task It_short_circuits_invalid_id_values_to_an_empty_page()
    {
        var referenceResolver = A.Fake<IReferenceResolver>();
        var result = await RelationalQueryRequestPreprocessor.PreprocessAsync(
            CreateMappingSet(),
            new QualifiedResourceName("Ed-Fi", "School"),
            [CreateQueryElement("ID", "$.id", "not-a-guid", "string")],
            CreateSupportedQueryCapability(
                CreateSupportedField("id", "$.id", "string", new RelationalQueryFieldTarget.DocumentUuid())
            ),
            referenceResolver
        );

        result
            .Outcome.Should()
            .BeOfType<RelationalQueryPreprocessingOutcome.EmptyPage>()
            .Which.Reason.Should()
            .Contain("ID");
        result.QueryElementsInOrder.Should().BeEmpty();
        A.CallTo(() => referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_parses_mixed_case_id_values_and_marks_document_uuid_join_requirement()
    {
        var referenceResolver = A.Fake<IReferenceResolver>();
        var documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        var result = await RelationalQueryRequestPreprocessor.PreprocessAsync(
            CreateMappingSet(),
            new QualifiedResourceName("Ed-Fi", "School"),
            [
                CreateQueryElement("ID", "$.id", documentUuid.ToString(), "string"),
                CreateQueryElement("SchoolId", "$.schoolId", "255901", "integer"),
            ],
            CreateSupportedQueryCapability(
                CreateSupportedField("id", "$.id", "string", new RelationalQueryFieldTarget.DocumentUuid()),
                CreateSupportedField(
                    "schoolId",
                    "$.schoolId",
                    "integer",
                    new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId"))
                )
            ),
            referenceResolver
        );

        result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
        result.QueryElementsInOrder.Should().HaveCount(2);
        result
            .QueryElementsInOrder[0]
            .Value.Should()
            .Be(new PreprocessedRelationalQueryValue.DocumentUuid(documentUuid));
        result.QueryElementsInOrder[0].SupportedField.QueryFieldName.Should().Be("id");
        result.QueryElementsInOrder[1].Value.Should().Be(new PreprocessedRelationalQueryValue.Raw("255901"));
        result.QueryElementsInOrder[1].SupportedField.QueryFieldName.Should().Be("schoolId");
        A.CallTo(() => referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_resolves_mixed_case_root_column_and_reference_identity_query_values_case_insensitively()
    {
        var referenceResolver = A.Fake<IReferenceResolver>();
        var result = await RelationalQueryRequestPreprocessor.PreprocessAsync(
            CreateMappingSet(),
            new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociations"),
            [
                CreateQueryElement("SchoolId", "$.schoolReference.schoolId", "255901", "integer"),
                CreateQueryElement(
                    "StudentUniqueId",
                    "$.studentReference.studentUniqueId",
                    "800000001",
                    "string"
                ),
            ],
            CreateSupportedQueryCapability(
                CreateSupportedField(
                    "schoolId",
                    "$.schoolReference.schoolId",
                    "integer",
                    new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId"))
                ),
                CreateSupportedField(
                    "studentUniqueId",
                    "$.studentReference.studentUniqueId",
                    "string",
                    new RelationalQueryFieldTarget.RootColumn(new DbColumnName("StudentUniqueId"))
                )
            ),
            referenceResolver
        );

        result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
        result.QueryElementsInOrder.Should().HaveCount(2);
        result.QueryElementsInOrder[0].Value.Should().Be(new PreprocessedRelationalQueryValue.Raw("255901"));
        result.QueryElementsInOrder[0].SupportedField.QueryFieldName.Should().Be("schoolId");
        result
            .QueryElementsInOrder[1]
            .Value.Should()
            .Be(new PreprocessedRelationalQueryValue.Raw("800000001"));
        result.QueryElementsInOrder[1].SupportedField.QueryFieldName.Should().Be("studentUniqueId");
        A.CallTo(() => referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_resolves_mixed_case_descriptor_query_values_in_one_batched_lookup_without_relookup()
    {
        var referenceResolver = A.Fake<IReferenceResolver>();
        ReferenceResolverRequest capturedRequest = null!;
        A.CallTo(() => referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedRequest = call.GetArgument<ReferenceResolverRequest>(0)!)
            .ReturnsLazily(
                (ReferenceResolverRequest request, CancellationToken _) =>
                    Task.FromResult(
                        CreateResolvedReferenceSet(
                            request
                                .DescriptorReferences.Select(
                                    (reference, index) =>
                                        new ResolvedDescriptorReference(reference, 800L + index, 31)
                                )
                                .ToArray()
                        )
                    )
            );

        var result = await RelationalQueryRequestPreprocessor.PreprocessAsync(
            CreateMappingSet(),
            new QualifiedResourceName("Ed-Fi", "School"),
            [
                CreateQueryElement(
                    "SchoolCategoryDescriptor",
                    "$.schoolCategoryDescriptor",
                    "uri://ONE",
                    "string"
                ),
                CreateQueryElement(
                    "AdministrativeFundingControlDescriptor",
                    "$.administrativeFundingControlDescriptor",
                    "uri://two",
                    "string"
                ),
            ],
            CreateSupportedQueryCapability(
                true,
                CreateSupportedField(
                    "schoolCategoryDescriptor",
                    "$.schoolCategoryDescriptor",
                    "string",
                    new RelationalQueryFieldTarget.DescriptorIdColumn(
                        new DbColumnName("SchoolCategoryDescriptorId"),
                        new QualifiedResourceName("Ed-Fi", "SchoolCategoryDescriptor")
                    )
                ),
                CreateSupportedField(
                    "administrativeFundingControlDescriptor",
                    "$.administrativeFundingControlDescriptor",
                    "string",
                    new RelationalQueryFieldTarget.DescriptorIdColumn(
                        new DbColumnName("AdministrativeFundingControlDescriptorId"),
                        new QualifiedResourceName("Ed-Fi", "AdministrativeFundingControlDescriptor")
                    )
                )
            ),
            referenceResolver
        );

        result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
        result.QueryElementsInOrder.Should().HaveCount(2);
        result
            .QueryElementsInOrder[0]
            .Value.Should()
            .Be(new PreprocessedRelationalQueryValue.DescriptorDocumentId(800L));
        result.QueryElementsInOrder[0].SupportedField.QueryFieldName.Should().Be("schoolCategoryDescriptor");
        result
            .QueryElementsInOrder[1]
            .Value.Should()
            .Be(new PreprocessedRelationalQueryValue.DescriptorDocumentId(801L));
        result
            .QueryElementsInOrder[1]
            .SupportedField.QueryFieldName.Should()
            .Be("administrativeFundingControlDescriptor");
        capturedRequest.DescriptorReferences.Should().HaveCount(2);
        capturedRequest
            .DescriptorReferences.Select(static reference =>
                reference.DocumentIdentity.DocumentIdentityElements.Single().IdentityValue
            )
            .Should()
            .Equal("uri://one", "uri://two");
    }

    [Test]
    public async Task It_short_circuits_unresolved_descriptor_queries_to_an_empty_page()
    {
        var referenceResolver = A.Fake<IReferenceResolver>();
        A.CallTo(() => referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                (ReferenceResolverRequest request, CancellationToken _) =>
                    Task.FromResult(
                        CreateResolvedReferenceSet(
                            invalidDescriptorReferences:
                            [
                                .. request.DescriptorReferences.Select(reference =>
                                    DescriptorReferenceFailure.From(
                                        reference,
                                        DescriptorReferenceFailureReason.Missing
                                    )
                                ),
                            ]
                        )
                    )
            );

        var result = await RelationalQueryRequestPreprocessor.PreprocessAsync(
            CreateMappingSet(),
            new QualifiedResourceName("Ed-Fi", "School"),
            [
                CreateQueryElement(
                    "SchoolCategoryDescriptor",
                    "$.schoolCategoryDescriptor",
                    "uri://missing",
                    "string"
                ),
            ],
            CreateSupportedQueryCapability(
                CreateSupportedField(
                    "schoolCategoryDescriptor",
                    "$.schoolCategoryDescriptor",
                    "string",
                    new RelationalQueryFieldTarget.DescriptorIdColumn(
                        new DbColumnName("SchoolCategoryDescriptorId"),
                        new QualifiedResourceName("Ed-Fi", "SchoolCategoryDescriptor")
                    )
                )
            ),
            referenceResolver
        );

        result
            .Outcome.Should()
            .BeOfType<RelationalQueryPreprocessingOutcome.EmptyPage>()
            .Which.Reason.Should()
            .Contain("SchoolCategoryDescriptor");
        result.QueryElementsInOrder.Should().BeEmpty();
    }

    private static QueryElement CreateQueryElement(
        string queryFieldName,
        string documentPath,
        string value,
        string type
    )
    {
        return new QueryElement(queryFieldName, [new JsonPath(documentPath)], value, type);
    }

    private static RelationalQueryCapability CreateSupportedQueryCapability(
        params SupportedRelationalQueryField[] supportedFields
    )
    {
        return CreateSupportedQueryCapability(false, supportedFields);
    }

    private static RelationalQueryCapability CreateSupportedQueryCapability(
        bool supportedFieldsThrowOnIndexerAccess,
        params SupportedRelationalQueryField[] supportedFields
    )
    {
        return new RelationalQueryCapability(
            new RelationalQuerySupport.Supported(),
            supportedFieldsThrowOnIndexerAccess
                ? new ThrowingIndexerReadOnlyDictionary<SupportedRelationalQueryField>(
                    supportedFields.ToDictionary(
                        static supportedField => supportedField.QueryFieldName,
                        static supportedField => supportedField,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                : supportedFields.ToDictionary(
                    static supportedField => supportedField.QueryFieldName,
                    static supportedField => supportedField,
                    StringComparer.OrdinalIgnoreCase
                ),
            new Dictionary<string, UnsupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase)
        );
    }

    private static SupportedRelationalQueryField CreateSupportedField(
        string queryFieldName,
        string path,
        string type,
        RelationalQueryFieldTarget target
    )
    {
        return new SupportedRelationalQueryField(
            queryFieldName,
            new RelationalQueryFieldPath(new JsonPathExpression(path, []), type),
            target
        );
    }

    private static MappingSet CreateMappingSet()
    {
        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: new DerivedRelationalModelSet(
                new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "5.2",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 0,
                    ResourceKeySeedHash: new byte[32],
                    SchemaComponentsInEndpointOrder: [],
                    ResourceKeysInIdOrder: []
                ),
                SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static ResolvedReferenceSet CreateResolvedReferenceSet(
        IReadOnlyList<ResolvedDescriptorReference>? successfulDescriptorReferences = null,
        IReadOnlyList<DescriptorReferenceFailure>? invalidDescriptorReferences = null
    )
    {
        successfulDescriptorReferences ??= [];
        invalidDescriptorReferences ??= [];

        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: successfulDescriptorReferences.ToDictionary(
                static reference => reference.Reference.Path,
                static reference => reference
            ),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: invalidDescriptorReferences,
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
    }

    private sealed class ThrowingIndexerReadOnlyDictionary<TValue>(
        IReadOnlyDictionary<string, TValue> innerDictionary
    ) : IReadOnlyDictionary<string, TValue>
    {
        public TValue this[string key] =>
            throw new AssertionException(
                $"Descriptor preprocessing should not use dictionary indexer access for key '{key}'."
            );

        public IEnumerable<string> Keys => innerDictionary.Keys;

        public IEnumerable<TValue> Values => innerDictionary.Values;

        public int Count => innerDictionary.Count;

        public bool ContainsKey(string key) => innerDictionary.ContainsKey(key);

        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => innerDictionary.GetEnumerator();

        public bool TryGetValue(string key, out TValue value)
        {
            if (innerDictionary.TryGetValue(key, out var resolvedValue))
            {
                value = resolvedValue;
                return true;
            }

            value = default!;
            return false;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

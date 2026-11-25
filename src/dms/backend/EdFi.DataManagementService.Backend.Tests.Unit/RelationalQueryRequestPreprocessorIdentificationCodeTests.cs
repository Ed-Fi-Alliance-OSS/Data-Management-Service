// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Identification-code collection filters preprocess as root filters do: a scalar value passes through
/// raw, a descriptor value is checked for well-formedness up front and resolved in the same single batched
/// lookup as root descriptors, and a SQL Server string value is bounded by the collection column's width.
/// </summary>
public partial class Given_RelationalQueryRequestPreprocessor
{
    private static readonly QualifiedResourceName _staff = new("Ed-Fi", "Staff");
    private static readonly DbTableName _staffIdentificationCodeTable = new(
        new DbSchemaName("edfi"),
        "StaffIdentificationCode"
    );
    private static readonly QualifiedResourceName _staffIdentificationSystemDescriptor = new(
        "Ed-Fi",
        "StaffIdentificationSystemDescriptor"
    );

    private const string CodePath = "$.identificationCodes[*].identificationCode";
    private const string SystemPath = "$.identificationCodes[*].staffIdentificationSystemDescriptor";

    [TestFixture]
    [Parallelizable]
    public class Given_Identification_Code_Collection_Filters
    {
        private static readonly SupportedRelationalQueryField _codeField = CreateSupportedField(
            "identificationCode",
            CodePath,
            "string",
            new RelationalQueryFieldTarget.CollectionColumn(
                _staffIdentificationCodeTable,
                new DbColumnName("Staff_DocumentId"),
                new DbColumnName("IdentificationCode"),
                DescriptorResource: null
            )
        );

        private static readonly SupportedRelationalQueryField _systemField = CreateSupportedField(
            "staffIdentificationSystemDescriptor",
            SystemPath,
            "string",
            new RelationalQueryFieldTarget.CollectionColumn(
                _staffIdentificationCodeTable,
                new DbColumnName("Staff_DocumentId"),
                new DbColumnName("StaffIdentificationSystemDescriptor_DescriptorId"),
                _staffIdentificationSystemDescriptor
            )
        );

        private static readonly SupportedRelationalQueryField _rootDescriptorField = CreateSupportedField(
            "sexDescriptor",
            "$.sexDescriptor",
            "string",
            new RelationalQueryFieldTarget.DescriptorIdColumn(
                new DbColumnName("SexDescriptor_DescriptorId"),
                new QualifiedResourceName("Ed-Fi", "SexDescriptor")
            )
        );

        private static Task<RelationalQueryPreprocessingResult> PreprocessAsync(
            IReferenceResolver referenceResolver,
            SqlDialect dialect,
            params QueryElement[] queryElements
        ) =>
            RelationalQueryRequestPreprocessor.PreprocessAsync(
                CreateMappingSetWithStaffIdentificationCodes(dialect, codeLength: 60),
                _staff,
                queryElements,
                CreateSupportedQueryCapability(
                    CreateSupportedField(
                        "id",
                        "$.id",
                        "string",
                        new RelationalQueryFieldTarget.DocumentUuid()
                    ),
                    _rootDescriptorField,
                    _codeField,
                    _systemField
                ),
                referenceResolver
            );

        private static IReferenceResolver CreateResolverResolvingEveryDescriptor(
            List<ReferenceResolverRequest> requests
        )
        {
            var referenceResolver = A.Fake<IReferenceResolver>();
            A.CallTo(() =>
                    referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._)
                )
                .ReturnsLazily(
                    (ReferenceResolverRequest request, CancellationToken _) =>
                    {
                        requests.Add(request);

                        return Task.FromResult(
                            CreateResolvedReferenceSet([
                                .. request.DescriptorReferences.Select(
                                    (reference, index) =>
                                        new ResolvedDescriptorReference(
                                            reference,
                                            40 + index,
                                            900L + index,
                                            31
                                        )
                                ),
                            ])
                        );
                    }
                );

            return referenceResolver;
        }

        [Test]
        public async Task It_passes_a_collection_scalar_value_through_raw_without_resolving()
        {
            var referenceResolver = A.Fake<IReferenceResolver>();

            var result = await PreprocessAsync(
                referenceResolver,
                SqlDialect.Pgsql,
                CreateQueryElement("IdentificationCode", CodePath, "ABC-123", "string")
            );

            result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
            result.QueryElementsInOrder.Single().SupportedField.Should().Be(_codeField);
            result
                .QueryElementsInOrder.Single()
                .Value.Should()
                .Be(new PreprocessedRelationalQueryValue.Raw("ABC-123"));
            A.CallTo(() =>
                    referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._)
                )
                .MustNotHaveHappened();
        }

        [Test]
        public async Task It_resolves_root_and_collection_descriptors_in_one_batched_lookup()
        {
            List<ReferenceResolverRequest> requests = [];

            var result = await PreprocessAsync(
                CreateResolverResolvingEveryDescriptor(requests),
                SqlDialect.Pgsql,
                CreateQueryElement(
                    "sexDescriptor",
                    "$.sexDescriptor",
                    "uri://ed-fi.org/SexDescriptor#F",
                    "string"
                ),
                CreateQueryElement("identificationCode", CodePath, "ABC-123", "string"),
                CreateQueryElement(
                    "staffIdentificationSystemDescriptor",
                    SystemPath,
                    "uri://ed-fi.org/StaffIdentificationSystemDescriptor#State",
                    "string"
                )
            );

            result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
            result
                .QueryElementsInOrder.Select(static element => element.Value)
                .Should()
                .Equal(
                    new PreprocessedRelationalQueryValue.DescriptorId(40),
                    new PreprocessedRelationalQueryValue.Raw("ABC-123"),
                    new PreprocessedRelationalQueryValue.DescriptorId(41)
                );
            requests.Should().ContainSingle();
            requests[0]
                .DescriptorReferences.Select(static reference =>
                    (
                        reference.ResourceInfo.ResourceName.Value,
                        reference.DocumentIdentity.DocumentIdentityElements.Single().IdentityValue
                    )
                )
                .Should()
                .Equal(
                    ("SexDescriptor", "uri://ed-fi.org/SexDescriptor#F"),
                    (
                        "StaffIdentificationSystemDescriptor",
                        "uri://ed-fi.org/StaffIdentificationSystemDescriptor#State"
                    )
                );
        }

        [Test]
        public async Task It_short_circuits_an_unresolved_collection_descriptor_to_an_empty_page()
        {
            var referenceResolver = A.Fake<IReferenceResolver>();
            A.CallTo(() =>
                    referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._)
                )
                .ReturnsLazily(
                    (ReferenceResolverRequest request, CancellationToken _) =>
                        Task.FromResult(
                            CreateResolvedReferenceSet(
                                invalidDescriptorReferences:
                                [
                                    .. request.DescriptorReferences.Select(static reference =>
                                        DescriptorReferenceFailure.From(
                                            reference,
                                            DescriptorReferenceFailureReason.Missing
                                        )
                                    ),
                                ]
                            )
                        )
                );

            var result = await PreprocessAsync(
                referenceResolver,
                SqlDialect.Pgsql,
                CreateQueryElement(
                    "staffIdentificationSystemDescriptor",
                    SystemPath,
                    "uri://ed-fi.org/StaffIdentificationSystemDescriptor#Missing",
                    "string"
                )
            );

            result
                .Outcome.Should()
                .BeOfType<RelationalQueryPreprocessingOutcome.EmptyPage>()
                .Which.Reason.Should()
                .Contain("staffIdentificationSystemDescriptor")
                .And.Contain("Ed-Fi.StaffIdentificationSystemDescriptor");
            result.QueryElementsInOrder.Should().BeEmpty();
        }

        [Test]
        public async Task It_rejects_a_malformed_collection_descriptor_even_after_a_filter_that_empties_the_page()
        {
            var referenceResolver = A.Fake<IReferenceResolver>();

            var result = await PreprocessAsync(
                referenceResolver,
                SqlDialect.Pgsql,
                CreateQueryElement("id", "$.id", "not-a-guid", "string"),
                CreateQueryElement(
                    "staffIdentificationSystemDescriptor",
                    SystemPath,
                    "uri://ed-fi.org/StaffIdentificationSystemDescriptor#\0",
                    "string"
                )
            );

            result
                .Outcome.Should()
                .BeOfType<RelationalQueryPreprocessingOutcome.ValidationFailure>()
                .Which.Failures.Should()
                .Equal(
                    new WriteValidationFailure(new JsonPath(SystemPath), DescriptorText.InvalidValueMessage)
                );
            result.QueryElementsInOrder.Should().BeEmpty();
            A.CallTo(() =>
                    referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._)
                )
                .MustNotHaveHappened();
        }

        [TestCase(SqlDialect.Mssql, 61, false)]
        [TestCase(SqlDialect.Mssql, 60, true)]
        [TestCase(SqlDialect.Pgsql, 61, true)]
        public async Task It_bounds_a_sql_server_collection_string_by_the_collection_column_width(
            SqlDialect dialect,
            int valueLength,
            bool accepted
        )
        {
            var value = new string('x', valueLength);

            // An earlier filter that would empty the page must not pre-empt the rejection.
            var result = await PreprocessAsync(
                A.Fake<IReferenceResolver>(),
                dialect,
                CreateQueryElement("id", "$.id", "not-a-guid", "string"),
                CreateQueryElement("identificationCode", CodePath, value, "string")
            );

            if (accepted)
            {
                result.Outcome.Should().NotBeOfType<RelationalQueryPreprocessingOutcome.ValidationFailure>();
                return;
            }

            result
                .Outcome.Should()
                .BeOfType<RelationalQueryPreprocessingOutcome.ValidationFailure>()
                .Which.Failures.Should()
                .Equal(
                    new WriteValidationFailure(
                        new JsonPath(CodePath),
                        $"The value '{value}' is not valid for identificationCode."
                    )
                );
        }

        [Test]
        public async Task It_does_not_bound_a_collection_descriptor_by_the_descriptor_column()
        {
            List<ReferenceResolverRequest> requests = [];
            var wideUri = $"uri://ed-fi.org/StaffIdentificationSystemDescriptor#{new string('x', 400)}";

            var result = await PreprocessAsync(
                CreateResolverResolvingEveryDescriptor(requests),
                SqlDialect.Mssql,
                CreateQueryElement("staffIdentificationSystemDescriptor", SystemPath, wideUri, "string")
            );

            result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
            requests.Should().ContainSingle();
        }
    }

    private static MappingSet CreateMappingSetWithStaffIdentificationCodes(SqlDialect dialect, int codeLength)
    {
        var edfi = new DbSchemaName("edfi");
        var root = new DbTableModel(
            new DbTableName(edfi, "Staff"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Staff",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
            ],
            []
        );
        var identificationCodes = new DbTableModel(
            _staffIdentificationCodeTable,
            new JsonPathExpression("$.identificationCodes[*]", []),
            new TableKey(
                "PK_StaffIdentificationCode",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("Staff_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("IdentificationCode"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, codeLength),
                    false,
                    new JsonPathExpression(CodePath, []),
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("StaffIdentificationSystemDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression(SystemPath, []),
                    _staffIdentificationSystemDescriptor
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            _staff,
            edfi,
            ResourceStorageKind.RelationalTables,
            root,
            [root, identificationCodes],
            [],
            []
        );
        var mappingSet = CreateMappingSet(dialect);

        return mappingSet with
        {
            Model = mappingSet.Model with
            {
                ConcreteResourcesInNameOrder =
                [
                    new ConcreteResourceModel(
                        new ResourceKeyEntry(1, _staff, "5.2.0", false),
                        ResourceStorageKind.RelationalTables,
                        model
                    ),
                ],
            },
        };
    }
}

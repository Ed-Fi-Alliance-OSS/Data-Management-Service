// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// A SQL Server string filter wider than its target column, in UTF-16 code units, is rejected as an invalid
/// filter value before anything is resolved or planned, even when it passes the field's declared maxLength
/// in text elements. The rule is SQL Server-specific; PostgreSQL is unchanged.
/// </summary>
public partial class Given_RelationalQueryRequestPreprocessor
{
    private static readonly QualifiedResourceName _student = new("Ed-Fi", "Student");

    // Three text elements, six UTF-16 code units.
    private const string ThreeSupplementaryCharacters = "\U0001F600\U0001F600\U0001F600";

    [TestFixture]
    [Parallelizable]
    public class Given_A_Root_String_Filter_And_Its_Column_Width
    {
        private static Task<RelationalQueryPreprocessingResult> PreprocessAsync(
            SqlDialect dialect,
            int? columnLength,
            string value,
            params QueryElement[] leadingElements
        ) =>
            RelationalQueryRequestPreprocessor.PreprocessAsync(
                CreateMappingSetWithStudentRoot(dialect, columnLength),
                _student,
                [.. leadingElements, CreateQueryElement("firstName", "$.firstName", value, "string")],
                CreateSupportedQueryCapability(
                    CreateSupportedField(
                        "id",
                        "$.id",
                        "string",
                        new RelationalQueryFieldTarget.DocumentUuid()
                    ),
                    CreateSupportedField(
                        "firstName",
                        "$.firstName",
                        "string",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("FirstName"))
                    )
                ),
                A.Fake<IReferenceResolver>()
            );

        [Test]
        public async Task It_rejects_a_sql_server_value_wider_than_the_column_in_utf16_code_units()
        {
            // An earlier filter that would empty the page must not pre-empt the rejection.
            var result = await PreprocessAsync(
                SqlDialect.Mssql,
                3,
                ThreeSupplementaryCharacters,
                CreateQueryElement("id", "$.id", "not-a-guid", "string")
            );

            result
                .Outcome.Should()
                .BeOfType<RelationalQueryPreprocessingOutcome.ValidationFailure>()
                .Which.Failures.Should()
                .Equal(
                    new WriteValidationFailure(
                        new JsonPath("$.firstName"),
                        $"The value '{ThreeSupplementaryCharacters}' is not valid for firstName."
                    )
                );
            result.QueryElementsInOrder.Should().BeEmpty();
        }

        [Test]
        public async Task It_accepts_a_sql_server_value_exactly_as_wide_as_the_column()
        {
            var result = await PreprocessAsync(SqlDialect.Mssql, 6, ThreeSupplementaryCharacters);

            result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
        }

        [Test]
        public async Task It_accepts_the_same_value_on_postgresql()
        {
            var result = await PreprocessAsync(SqlDialect.Pgsql, 3, ThreeSupplementaryCharacters);

            result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
            result
                .QueryElementsInOrder.Single()
                .Value.Should()
                .Be(new PreprocessedRelationalQueryValue.Raw(ThreeSupplementaryCharacters));
        }

        [Test]
        public async Task It_does_not_bound_an_unbounded_sql_server_column()
        {
            var result = await PreprocessAsync(SqlDialect.Mssql, null, new string('x', 5000));

            result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Descriptor_Reference_Filter_And_The_Stored_Uri_Width
    {
        // 307 UTF-16 code units: one wider than the longest stored namespace#codeValue.
        private static readonly string _overWideUri =
            "uri://ed-fi.org/SchoolCategoryDescriptor#"
            + new string('x', NaturalKeyLookupLimits.MaxDescriptorUriLength + 1 - 41);

        private static async Task<(
            RelationalQueryPreprocessingResult Result,
            IReferenceResolver Resolver
        )> PreprocessAsync(SqlDialect dialect)
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
                CreateMappingSet(dialect),
                new QualifiedResourceName("Ed-Fi", "School"),
                [
                    CreateQueryElement(
                        "schoolCategoryDescriptor",
                        "$.schoolCategoryDescriptor",
                        _overWideUri,
                        "string"
                    ),
                ],
                CreateSupportedQueryCapability(CreateSchoolCategoryDescriptorField()),
                referenceResolver
            );

            return (result, referenceResolver);
        }

        [Test]
        public async Task It_rejects_a_sql_server_uri_wider_than_any_stored_uri_without_resolving()
        {
            _overWideUri.Length.Should().Be(NaturalKeyLookupLimits.MaxDescriptorUriLength + 1);

            var (result, referenceResolver) = await PreprocessAsync(SqlDialect.Mssql);

            result
                .Outcome.Should()
                .BeOfType<RelationalQueryPreprocessingOutcome.ValidationFailure>()
                .Which.Failures.Should()
                .Equal(
                    new WriteValidationFailure(
                        new JsonPath("$.schoolCategoryDescriptor"),
                        $"The value '{_overWideUri}' is not valid for schoolCategoryDescriptor."
                    )
                );
            A.CallTo(() =>
                    referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._)
                )
                .MustNotHaveHappened();
        }

        [Test]
        public async Task It_resolves_the_same_uri_on_postgresql()
        {
            var (result, referenceResolver) = await PreprocessAsync(SqlDialect.Pgsql);

            result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.EmptyPage>();
            A.CallTo(() =>
                    referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    private static MappingSet CreateMappingSetWithStudentRoot(SqlDialect dialect, int? firstNameLength)
    {
        var root = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Student"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Student",
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
                new DbColumnModel(
                    new DbColumnName("FirstName"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, firstNameLength),
                    false,
                    new JsonPathExpression("$.firstName", []),
                    null
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            _student,
            root.Table.Schema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
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
                        new ResourceKeyEntry(1, _student, "5.2.0", false),
                        ResourceStorageKind.RelationalTables,
                        model
                    ),
                ],
            },
        };
    }
}

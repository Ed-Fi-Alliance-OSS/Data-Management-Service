// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

public partial class Given_RelationalQueryRequestPreprocessor
{
    [TestFixture(SqlDialect.Pgsql, "direct")]
    [TestFixture(SqlDialect.Mssql, "direct")]
    [TestFixture(SqlDialect.Pgsql, "reference-identity")]
    [TestFixture(SqlDialect.Mssql, "reference-identity")]
    [TestFixture(SqlDialect.Pgsql, "unified-reference-identity")]
    [TestFixture(SqlDialect.Mssql, "unified-reference-identity")]
    public class Given_CompactDescriptorFilters(SqlDialect dialect, string shape)
    {
        private static readonly QualifiedResourceName DescriptorResource = new(
            "Ed-Fi",
            "AcademicSubjectDescriptor"
        );
        private static readonly (string Namespace, string CodeValue, int DescriptorId)[] StoredDescriptors =
        [
            ("uri://Example.org/Kind", "MiXeD", 42),
            ("uri://Example.org/Kind ", "MiXeD", 43),
            ("uri://Example.org/Kind#Additional", "Value", 44),
            ("uri://Example.org/Kind", "MiXeD%23Value", 45),
            ("uri://Example.org/Kind", "MiXeD%2523Value", 46),
            ("uri://Example.org/Kind", "Caf\u00e9", 47),
            ("uri://Example.org/Kind", "Cafe\u0301", 48),
        ];

        // Values have already crossed the HTTP percent-decoding boundary. Literal escapes stay literal here.
        private static readonly string[] QueryUris =
        [
            "uri://Example.org/Kind#MiXeD",
            "URI://EXAMPLE.ORG/KIND#MIXED",
            "uri://Example.org/Kind #MiXeD",
            "uri://Example.org/Kind#MiXeD",
            "uri://Example.org/Kind#Additional" + "#" + "Value",
            "uri://Example.org/Kind" + "#" + "Additional#Value",
            "uri://Example.org/Kind#MiXeD%23Value",
            "uri://Example.org/Kind#MiXeD%2523Value",
            "uri://Example.org/Kind#Caf\u00e9",
            "uri://Example.org/Kind#Cafe\u0301",
        ];
        private static readonly int[] ExpectedIds = [42, 42, 43, 42, 44, 44, 45, 46, 47, 48];
        private RelationalQueryPreprocessingResult _preprocessed = null!;
        private IReadOnlyList<PageKeysetSpec.Query> _keysets = null!;
        private string _queryPath = null!;
        private ReferenceResolverRequest _lookupRequest = null!;
        private IReferenceResolver _resolver = null!;
        private string _filterColumn = null!;

        [SetUp]
        public async Task CompilePreprocessAndPlan()
        {
            var resource = new QualifiedResourceName("Ed-Fi", "StudentAssociation");
            var root = CreateRoot();
            var isReferenceIdentity = shape != "direct";
            _queryPath = isReferenceIdentity
                ? "$.studentReference.academicSubjectDescriptor"
                : "$.academicSubjectDescriptor";
            var publicPath = new JsonPathExpression(_queryPath, []);
            var storagePath = publicPath;
            List<ReferenceIdentityBinding> identityBindings =
            [
                new(
                    new JsonPathExpression("$.academicSubjectDescriptor", []),
                    storagePath,
                    new DbColumnName(_filterColumn)
                ),
            ];
            List<DescriptorEdgeSource> descriptorEdges =
            [
                new(false, storagePath, root.Table, new DbColumnName(_filterColumn), DescriptorResource),
            ];
            if (shape == "unified-reference-identity")
            {
                var duplicateColumn = new DbColumnName("DuplicateDescriptorAliasId");
                identityBindings.Add(
                    new(
                        new JsonPathExpression("$.academicSubjectDescriptor", []),
                        storagePath,
                        duplicateColumn
                    )
                );
                descriptorEdges.Add(new(false, storagePath, root.Table, duplicateColumn, DescriptorResource));
            }
            var model = new RelationalResourceModel(
                resource,
                root.Table.Schema,
                ResourceStorageKind.RelationalTables,
                root,
                [root],
                isReferenceIdentity
                    ?
                    [
                        new DocumentReferenceBinding(
                            false,
                            new JsonPathExpression("$.studentReference", []),
                            root.Table,
                            new DbColumnName("Student_DocumentId"),
                            new QualifiedResourceName("Ed-Fi", "Student"),
                            identityBindings
                        ),
                    ]
                    : [],
                descriptorEdges
            );
            var concreteResource = new ConcreteResourceModel(
                new ResourceKeyEntry(1, resource, "5.2.0", false),
                ResourceStorageKind.RelationalTables,
                model
            )
            {
                QueryFieldMappingsByQueryField = QueryUris
                    .Select(
                        (_, index) =>
                            new RelationalQueryFieldMapping(
                                $"descriptor{index}",
                                [new RelationalQueryFieldPath(publicPath, "string")]
                            )
                    )
                    .ToDictionary(field => field.QueryFieldName),
            };
            var capability = new RelationalQueryCapabilityCompiler().Compile(concreteResource);
            capability.Support.Should().BeOfType<RelationalQuerySupport.Supported>();

            var catalog = StoredDescriptors.ToDictionary(
                descriptor =>
                    CreateReferentialId($"{descriptor.Namespace}#{descriptor.CodeValue}".ToLowerInvariant()),
                descriptor => descriptor.DescriptorId
            );
            _resolver = A.Fake<IReferenceResolver>();
            A.CallTo(() => _resolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
                .Invokes(call => _lookupRequest = call.GetArgument<ReferenceResolverRequest>(0)!)
                .ReturnsLazily(
                    (ReferenceResolverRequest request, CancellationToken _) =>
                        Task.FromResult(
                            CreateResolvedReferenceSet(
                                request
                                    .DescriptorReferences.Select(reference => new ResolvedDescriptorReference(
                                        reference,
                                        catalog[reference.ReferentialId],
                                        5000000000L + catalog[reference.ReferentialId],
                                        31
                                    ))
                                    .ToArray()
                            )
                        )
                );
            _preprocessed = await RelationalQueryRequestPreprocessor.PreprocessAsync(
                CreateMappingSet(dialect),
                resource,
                QueryUris
                    .Select(
                        (uri, index) =>
                            CreateQueryElement($"descriptor{index}", publicPath.Canonical, uri, "string")
                    )
                    .ToArray(),
                capability,
                _resolver
            );
            // Each alias names the same column; plan one filter at a time to honor duplicate-predicate rejection.
            _keysets = _preprocessed
                .QueryElementsInOrder.Select(element =>
                    new RelationalQueryPageKeysetPlanner(dialect).Plan(
                        root,
                        new RelationalQueryPreprocessingResult(_preprocessed.Outcome, [element]),
                        new CollectionPaging.Traditional(
                            new PaginationParameters(
                                Limit: 25,
                                Offset: 0,
                                TotalCount: true,
                                MaximumPageSize: 500
                            )
                        )
                    )
                )
                .ToArray();
        }

        [Test]
        public void It_preserves_whole_uri_RI_normalization_and_resolves_exact_compact_ids_once()
        {
            _preprocessed.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
            _preprocessed
                .QueryElementsInOrder.Select(element => element.Value)
                .Should()
                .Equal(ExpectedIds.Select(id => new PreprocessedRelationalQueryValue.DescriptorId(id)));
            _lookupRequest
                .DescriptorReferences.Select(reference =>
                    reference.DocumentIdentity.DocumentIdentityElements.Single().IdentityValue
                )
                .Should()
                .Equal(QueryUris.Select(uri => uri.ToLowerInvariant()));
            _lookupRequest
                .DescriptorReferences.Select(reference => reference.ReferentialId)
                .Should()
                .Equal(QueryUris.Select(uri => CreateReferentialId(uri.ToLowerInvariant())));
            _lookupRequest.DocumentReferences.Should().BeEmpty();
            A.CallTo(() => _resolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_filters_the_compact_local_column_in_page_and_count_SQL()
        {
            var filterColumn =
                shape == "unified-reference-identity"
                    ? "AcademicSubjectDescriptorCanonicalId"
                    : _filterColumn;
            var quotedColumn = dialect is SqlDialect.Pgsql ? $"\"{filterColumn}\"" : $"[{filterColumn}]";
            for (var index = 0; index < QueryUris.Length; index++)
            {
                var keyset = _keysets[index];
                keyset.Plan.PageDocumentIdSql.Should().Contain($"{quotedColumn} = @descriptor{index}");
                keyset.Plan.TotalCountSql.Should().Contain($"{quotedColumn} = @descriptor{index}");
                keyset
                    .ParameterValues[$"descriptor{index}"]
                    .Should()
                    .BeOfType<int>()
                    .Which.Should()
                    .Be(ExpectedIds[index]);
                _preprocessed
                    .QueryElementsInOrder[index]
                    .SupportedField.Path.Path.Canonical.Should()
                    .Be(_queryPath);
                keyset.ParameterValues["offset"].Should().BeOfType<long>().Which.Should().Be(0L);
                keyset.ParameterValues["limit"].Should().BeOfType<long>().Which.Should().Be(25L);
                if (shape == "unified-reference-identity")
                {
                    var presenceColumn =
                        dialect is SqlDialect.Pgsql ? "\"Student_DocumentId\"" : "[Student_DocumentId]";
                    keyset.Plan.PageDocumentIdSql.Should().Contain($"{presenceColumn} IS NOT NULL");
                    keyset.Plan.TotalCountSql.Should().Contain($"{presenceColumn} IS NOT NULL");
                }
            }
        }

        [Test]
        public void It_binds_provider_integer_filter_parameters_and_bigint_paging_parameters()
        {
            using var dataSource = NpgsqlDataSource.Create("Host=localhost");
            using var connection = dataSource.CreateConnection();
            for (var index = 0; index < QueryUris.Length; index++)
            {
                using DbCommand command =
                    dialect is SqlDialect.Pgsql ? connection.CreateCommand() : new SqlCommand();
                HydrationBatchBuilder.AddParameters(command, _keysets[index]);
                command.Parameters.Count.Should().Be(3);
                var parameter = command.Parameters[$"@descriptor{index}"];
                parameter.Value.Should().BeOfType<int>().Which.Should().Be(ExpectedIds[index]);
                parameter.DbType.Should().Be(DbType.Int32);
                if (parameter is NpgsqlParameter pgsql)
                {
                    pgsql.NpgsqlDbType.Should().Be(NpgsqlDbType.Integer);
                }
                else
                {
                    ((SqlParameter)parameter).SqlDbType.Should().Be(SqlDbType.Int);
                }
                command.Parameters["@offset"].DbType.Should().Be(DbType.Int64);
                command.Parameters["@limit"].DbType.Should().Be(DbType.Int64);
            }
        }

        private DbTableModel CreateRoot()
        {
            var isReferenceIdentity = shape != "direct";
            _filterColumn = isReferenceIdentity
                ? "Student_AcademicSubjectDescriptorId"
                : "AcademicSubjectDescriptorId";
            var canonicalColumn = new DbColumnName("AcademicSubjectDescriptorCanonicalId");
            var descriptor = new DbColumnModel(
                new DbColumnName(_filterColumn),
                ColumnKind.DescriptorFk,
                new RelationalScalarType(ScalarKind.Int32),
                false,
                new JsonPathExpression(
                    isReferenceIdentity
                        ? "$.studentReference.academicSubjectDescriptor"
                        : "$.academicSubjectDescriptor",
                    []
                ),
                DescriptorResource,
                shape == "unified-reference-identity"
                    ? new ColumnStorage.UnifiedAlias(canonicalColumn, new DbColumnName("Student_DocumentId"))
                    : new ColumnStorage.Stored()
            );
            return new DbTableModel(
                new DbTableName(new DbSchemaName("edfi"), "StudentAssociation"),
                new JsonPathExpression("$", []),
                new TableKey(
                    "PK_StudentAssociation",
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
                        new DbColumnName("Student_DocumentId"),
                        ColumnKind.DocumentFk,
                        new RelationalScalarType(ScalarKind.Int64),
                        false,
                        new JsonPathExpression("$.studentReference", []),
                        new QualifiedResourceName("Ed-Fi", "Student")
                    ),
                    .. shape == "unified-reference-identity"
                        ? new[]
                        {
                            descriptor with
                            {
                                ColumnName = canonicalColumn,
                                SourceJsonPath = null,
                                Storage = new ColumnStorage.Stored(),
                            },
                        }
                        : [],
                    descriptor,
                    .. shape == "unified-reference-identity"
                        ? new[]
                        {
                            descriptor with
                            {
                                ColumnName = new DbColumnName("DuplicateDescriptorAliasId"),
                            },
                        }
                        : [],
                ],
                []
            );
        }

        private static ReferentialId CreateReferentialId(string normalizedUri) =>
            ReferentialIdFactory.Create(
                new BaseResourceInfo(
                    new ProjectName(DescriptorResource.ProjectName),
                    new ResourceName(DescriptorResource.ResourceName),
                    true
                ),
                new DocumentIdentity([
                    new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, normalizedUri),
                ])
            );
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture(SqlDialect.Pgsql, "direct")]
[TestFixture(SqlDialect.Mssql, "direct")]
[TestFixture(SqlDialect.Pgsql, "unified")]
[TestFixture(SqlDialect.Mssql, "unified")]
[TestFixture(SqlDialect.Pgsql, "copied")]
[TestFixture(SqlDialect.Mssql, "copied")]
[TestFixture(SqlDialect.Pgsql, "unified-copy")]
[TestFixture(SqlDialect.Mssql, "unified-copy")]
public class Given_WritePlanCompiler_CompactDescriptorBindings(SqlDialect dialect, string shape)
    : WritePlanCompilerTestBase
{
    private const int DescriptorId = 42;
    private const long DescriptorDocumentId = 5000000042L;
    private const long RootDocumentId = 6000000001L;
    private const long ReferencedDocumentId = 7000000002L;
    private const string Uri = "uri://Example.org/Kind #MiXeD#Value";
    private TableWritePlan _tablePlan = null!;
    private RelationalCommand _rowCommand = null!;
    private RelationalCommand _batchCommand = null!;

    [SetUp]
    public void CompileAndFlatten()
    {
        var model = shape switch
        {
            "direct" => CreateDirectDescriptorModel(),
            "unified" => CreateRootOnlyModelWithCompiledKeyUnificationInventory(),
            "copied" => ReferenceDerivedWritePlanFixture.CreateDescriptorBackedModel(),
            "unified-copy" => ReferenceDerivedWritePlanFixture.CreateDescriptorBackedKeyUnificationModel(),
            _ => throw new InvalidOperationException($"Unknown shape '{shape}'."),
        };
        var root = model.Root with
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new("DocumentId")],
                [new("DocumentId")],
                [],
                []
            ),
        };
        model = model with
        {
            Root = root,
            TablesInDependencyOrder = [root],
            DescriptorEdgeSources =
            [
                .. root
                    .Columns.Where(column =>
                        column.Kind is ColumnKind.DescriptorFk && column.SourceJsonPath is not null
                    )
                    .Select(column => new DescriptorEdgeSource(
                        false,
                        column.SourceJsonPath!.Value,
                        root.Table,
                        column.ColumnName,
                        column.TargetResource!.Value
                    )),
            ],
        };
        var plan = new WritePlanCompiler(dialect).Compile(model);
        _tablePlan = plan.TablePlansInDependencyOrder.Single();
        var descriptorReferences = model
            .Root.Columns.Where(column =>
                column.Kind is ColumnKind.DescriptorFk && column.SourceJsonPath is not null
            )
            .ToDictionary(
                column => new JsonPath(column.SourceJsonPath!.Value.Canonical),
                column => new ResolvedDescriptorReference(
                    new DescriptorReference(
                        new BaseResourceInfo(
                            new ProjectName(column.TargetResource!.Value.ProjectName),
                            new ResourceName(column.TargetResource.Value.ResourceName),
                            true
                        ),
                        new DocumentIdentity([new(DocumentIdentity.DescriptorIdentityJsonPath, Uri)]),
                        new ReferentialId(Guid.NewGuid()),
                        new JsonPath(column.SourceJsonPath!.Value.Canonical)
                    ),
                    DescriptorId,
                    DescriptorDocumentId,
                    3
                )
            );
        var documentReferences = model.DocumentReferenceBindings.ToDictionary(
            binding => new JsonPath(binding.ReferenceObjectPath.Canonical),
            binding => new ResolvedDocumentReference(
                new DocumentReference(
                    new BaseResourceInfo(
                        new ProjectName(binding.TargetResource.ProjectName),
                        new ResourceName(binding.TargetResource.ResourceName),
                        false
                    ),
                    new DocumentIdentity([
                        .. binding.IdentityBindings.Select(identity => new DocumentIdentityElement(
                            new JsonPath(identity.IdentityJsonPath.Canonical),
                            Uri
                        )),
                    ]),
                    new ReferentialId(Guid.NewGuid()),
                    new JsonPath(binding.ReferenceObjectPath.Canonical)
                ),
                ReferencedDocumentId,
                2
            )
        );
        var references = new ResolvedReferenceSet(
            documentReferences,
            descriptorReferences,
            new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            [],
            [],
            [],
            []
        );
        var body = new JsonObject
        {
            ["schoolYear"] = 2026,
            ["localSchoolYear"] = 2026,
            ["schoolYearTypeDescriptor"] = Uri,
            ["localSchoolYearTypeDescriptor"] = Uri,
            ["kindDescriptor"] = Uri,
            ["schoolReference"] = new JsonObject { ["schoolCategoryDescriptor"] = Uri },
        };
        var flattened = new RelationalWriteFlattener().Flatten(
            new FlatteningInput(
                RelationalWriteOperationKind.Post,
                new RelationalWriteTargetContext.ExistingDocument(RootDocumentId, new(Guid.NewGuid())),
                plan,
                body,
                references
            )
        );
        var row = new RelationalWriteMergedTableRow(flattened.RootRow.Values, flattened.RootRow.Values);
        var itemIds = RelationalWriteCollectionItemIdBindings.Create(
            dialect,
            new RelationalWriteMergeResult([], supportsGuardedNoOp: true)
        );
        var rootId = new RelationalWriteRootDocumentIdSource.Bound(RootDocumentId);
        _rowCommand = RelationalWriteRowStatements.BuildRowCommand(
            _tablePlan,
            _tablePlan.InsertSql,
            row,
            rootId,
            itemIds
        );
        _batchCommand = RelationalWriteRowStatements.BuildBatchCommand(
            new WritePlanBatchSqlEmitter(dialect).EmitInsertBatch(_tablePlan, 2),
            _tablePlan,
            [row, row],
            0,
            2,
            rootId,
            itemIds
        );
    }

    [Test]
    public void It_binds_compact_descriptor_parameters_in_single_and_batched_writes()
    {
        var binding = _tablePlan.ColumnBindings.Single(binding =>
            binding.Column.Kind is ColumnKind.DescriptorFk
        );
        binding.Column.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int32));
        AssertParameter(_rowCommand, $"@{binding.ParameterName}", DescriptorId);
        AssertParameter(_batchCommand, $"@{binding.ParameterName}_0", DescriptorId);
        AssertParameter(_batchCommand, $"@{binding.ParameterName}_1", DescriptorId);
    }

    [Test]
    public void It_retains_wide_document_parameters()
    {
        foreach (
            var binding in _tablePlan.ColumnBindings.Where(binding =>
                binding.Source is WriteValueSource.DocumentId or WriteValueSource.DocumentReference
            )
        )
        {
            var expected =
                binding.Source is WriteValueSource.DocumentId ? RootDocumentId : ReferencedDocumentId;
            AssertParameter(_rowCommand, $"@{binding.ParameterName}", expected);
        }
    }

    private void AssertParameter(RelationalCommand command, string name, object expected)
    {
        command.CommandText.Should().Contain(name);
        var parameter = command.Parameters.Single(parameter => parameter.Name == name);
        parameter.Value.Should().BeOfType(expected.GetType());
        parameter.Value.Should().Be(expected);
        if (dialect is SqlDialect.Pgsql)
        {
            using var source = NpgsqlDataSource.Create("Host=localhost");
            using var connection = source.CreateConnection();
            using var providerCommand = connection.CreateCommand();
            var pgsql = providerCommand.CreateParameter();
            pgsql.Value = parameter.Value;
            pgsql.NpgsqlDbType.Should().Be(expected is int ? NpgsqlDbType.Integer : NpgsqlDbType.Bigint);
        }
        else
        {
            var mssql = new SqlParameter { Value = parameter.Value };
            mssql
                .SqlDbType.Should()
                .Be(expected is int ? System.Data.SqlDbType.Int : System.Data.SqlDbType.BigInt);
        }
    }

    private static RelationalResourceModel CreateDirectDescriptorModel()
    {
        var model = CreateSupportedRootOnlyModel();
        var path = CreatePath("$.kindDescriptor", new JsonPathSegment.Property("kindDescriptor"));
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "KindDescriptor");
        var column = new DbColumnModel(
            new("Kind_DescriptorId"),
            ColumnKind.DescriptorFk,
            new(ScalarKind.Int32),
            IsNullable: false,
            path,
            descriptorResource
        );
        var root = model.Root with { Columns = [model.Root.Columns[0], column] };
        return model with
        {
            Root = root,
            TablesInDependencyOrder = [root],
            DescriptorEdgeSources = [new(false, path, root.Table, column.ColumnName, descriptorResource)],
        };
    }
}

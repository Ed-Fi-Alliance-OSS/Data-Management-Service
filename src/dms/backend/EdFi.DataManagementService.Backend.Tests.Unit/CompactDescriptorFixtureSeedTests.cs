// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture(MaterializedDocumentFixtureSqlDialect.Postgresql)]
[TestFixture(MaterializedDocumentFixtureSqlDialect.Mssql)]
public class Given_Compact_Descriptor_Fixture_Seeds(MaterializedDocumentFixtureSqlDialect dialect)
{
    private MaterializedDocumentFixture _fixture = null!;
    private IReadOnlyList<MaterializedDocumentFixtureSqlCommand> _commands = null!;
    private FixtureSeedConnection _connection = null!;
    private IReadOnlyDictionary<int, SeededDescriptor> _seeded = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = MaterializedDocumentFixtureCatalog.LoadCase(
            TestContext.CurrentContext.TestDirectory,
            "extension-student-school-association"
        );
        var seeder = new MaterializedDocumentFixtureSeeder(dialect);
        _commands = seeder.BuildSetupCommands(_fixture);
        _connection = new();
        _seeded = await seeder.SeedAsync(_connection, _fixture);
    }

    [Test]
    public void It_uses_native_int_descriptor_identity_and_keeps_the_unique_bigint_document_association()
    {
        var table = _commands.Single(command =>
            command.CommandText.Contains("CREATE TABLE", StringComparison.Ordinal)
            && command.CommandText.Contains(Quote("Descriptor") + " (", StringComparison.Ordinal)
        );
        table
            .CommandText.Should()
            .Contain(
                Quote("DescriptorId")
                    + (
                        dialect == MaterializedDocumentFixtureSqlDialect.Postgresql
                            ? " int GENERATED ALWAYS AS IDENTITY"
                            : " int IDENTITY(1,1)"
                    )
            );
        table.CommandText.Should().Contain(Quote("DocumentId") + " bigint NOT NULL UNIQUE REFERENCES");
        table.CommandText.Should().NotContain(Quote("Discriminator")).And.NotContain(Quote("Uri"));
        _commands
            .Where(command => command.DescriptorKey is not null)
            .Should()
            .HaveCount(3)
            .And.AllSatisfy(command =>
            {
                command.CommandText.Should().NotContain(Quote("Discriminator")).And.NotContain(Quote("Uri"));
                command
                    .CommandText.Should()
                    .Contain(
                        dialect == MaterializedDocumentFixtureSqlDialect.Postgresql
                            ? "RETURNING \"DescriptorId\""
                            : "OUTPUT inserted.[DescriptorId] INTO @descriptor"
                    );
            });
    }

    [Test]
    public void It_binds_the_generated_compact_keys_in_root_child_and_extension_rows()
    {
        AssertDescriptorReference("StudentSchoolAssociation", 103);
        AssertDescriptorReference("StudentSchoolAssociationEducationPlan", 102);
        AssertDescriptorReference("StudentSchoolAssociationExtension", 101);
        _commands
            .Where(command =>
                command.CommandText.Contains("CREATE TABLE", StringComparison.Ordinal)
                && command.CommandText.Contains("_DescriptorId", StringComparison.Ordinal)
            )
            .Should()
            .AllSatisfy(command =>
                command
                    .CommandText.Should()
                    .Contain(
                        "_DescriptorId"
                            + (dialect == MaterializedDocumentFixtureSqlDialect.Postgresql ? "\"" : "]")
                            + " integer NULL"
                    )
            );
    }

    [Test]
    public void It_keeps_referential_identity_and_document_stamps_keyed_by_the_owning_document()
    {
        foreach (var source in _fixture.SourceSetup.Descriptors)
        {
            var keys = _seeded[source.DescriptorId];
            keys.DocumentId.Should().Be(source.DocumentId);
            keys.DescriptorId.Should().NotBe(source.DescriptorId);
            ((long)keys.DescriptorId).Should().NotBe(source.DocumentId);
            _connection
                .Commands.Where(command =>
                    command.CommandText.Contains(
                        "INSERT INTO " + Quote("dms") + "." + Quote("ReferentialIdentity"),
                        StringComparison.Ordinal
                    )
                )
                .Should()
                .Contain(command =>
                    command.Parameters.Any(parameter => Equals(parameter.Value, source.DocumentId))
                );
            _connection
                .Commands.Where(command =>
                    command.CommandText.StartsWith(
                        "UPDATE " + Quote("dms") + "." + Quote("Document"),
                        StringComparison.Ordinal
                    )
                )
                .Should()
                .Contain(command =>
                    command.Parameters.Any(parameter => Equals(parameter.Value, source.DocumentId))
                );
        }
    }

    [Test]
    public void It_compiles_cache_mapping_descriptor_keys_and_reference_columns_as_int32()
    {
        var mapping = DocumentCacheMaterializerFixtureMappingSet.CreateExtensionFixture(
            dialect == MaterializedDocumentFixtureSqlDialect.Postgresql ? SqlDialect.Pgsql : SqlDialect.Mssql
        );
        var descriptor = mapping
            .Model.ConcreteResourcesInNameOrder.First(model =>
                model.StorageKind == ResourceStorageKind.SharedDescriptorTable
            )
            .RelationalModel.Root;
        descriptor.Key.Columns.Select(column => column.ColumnName.Value).Should().Equal("DescriptorId");
        descriptor
            .Columns.Single(column => column.ColumnName.Value == "DescriptorId")
            .ScalarType!.Kind.Should()
            .Be(ScalarKind.Int32);
        mapping
            .Model.ConcreteResourcesInNameOrder.SelectMany(model =>
                model.RelationalModel.TablesInDependencyOrder
            )
            .SelectMany(table => table.Columns)
            .Where(column => column.Kind == ColumnKind.DescriptorFk)
            .Should()
            .HaveCount(3)
            .And.AllSatisfy(column => column.ScalarType!.Kind.Should().Be(ScalarKind.Int32));
    }

    private string Quote(string identifier) =>
        dialect == MaterializedDocumentFixtureSqlDialect.Postgresql ? $"\"{identifier}\"" : $"[{identifier}]";

    private void AssertDescriptorReference(string table, int fixtureKey)
    {
        var command = _connection.Commands.Single(command =>
            command.CommandText.StartsWith("INSERT INTO ", StringComparison.Ordinal)
            && command.CommandText.Contains(Quote(table) + " (", StringComparison.Ordinal)
        );
        command
            .Parameters.Should()
            .Contain(parameter =>
                parameter.Value is int && Equals(parameter.Value, _seeded[fixtureKey].DescriptorId)
            );
        command
            .Parameters.Should()
            .NotContain(parameter => Equals(parameter.Value, _seeded[fixtureKey].DocumentId));
    }

    private sealed class FixtureSeedConnection : DbConnection
    {
        public List<RecordingDbCommand> Commands { get; } = [];

        [AllowNull]
        public override string ConnectionString { get; set; } = "fixture";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() { }

        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand()
        {
            var command = new RecordingDbCommand(new DataTable().CreateDataReader())
            {
                ScalarResult = 700 + Commands.Count,
            };
            Commands.Add(command);
            return command;
        }
    }
}

[TestFixture]
public class Given_Reusable_Compact_Descriptor_Fixture_Uri_Cases
{
    private IReadOnlyList<CompactDescriptorUriCase> _cases = null!;
    private CompactDescriptorAuthorizationFixture _synthetic = null!;

    [SetUp]
    public void Setup()
    {
        _cases = CompactDescriptorUriCase.Cases;
        _synthetic = new CompactDescriptorAuthorizationFixture(SqlDialect.Pgsql);
    }

    [Test]
    public void It_preserves_mixed_case_cross_type_cross_project_and_whole_uri_collision_cases()
    {
        var cases = _cases;
        cases
            .Take(3)
            .Select(value => value.Uri)
            .Should()
            .Equal(Enumerable.Repeat("uri://Example.org/SchoolTypeDescriptor#MiXeD", 3));
        cases
            .Take(3)
            .Select(value => (value.ProjectName, value.ResourceName))
            .Distinct()
            .Should()
            .HaveCount(3);
        var synthetic = _synthetic;
        synthetic
            .MappingSet.ResourceKeyIdByResource.Should()
            .ContainKey(new(cases[0].ProjectName, cases[0].ResourceName))
            .And.ContainKey(new(cases[2].ProjectName, cases[2].ResourceName));
        cases[3].Uri.Should().Be("uri://example.org/a#b#c").And.Be(cases[4].Uri);
        cases[3].Namespace.Should().NotBe(cases[4].Namespace);
        cases[5].Uri.Should().Be("uri://example.org/a #c").And.NotBe(cases[6].Uri);
    }
}

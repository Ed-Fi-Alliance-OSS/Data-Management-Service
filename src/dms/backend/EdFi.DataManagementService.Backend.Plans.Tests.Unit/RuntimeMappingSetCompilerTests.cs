// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_RuntimeMappingSetCompiler
{
    private const string MissingSemanticIdentityFixturePath =
        "Fixtures/runtime-plan-compilation/focused-stable-key/negative/missing-semantic-identity/fixture.manifest.json";
    private const string PositiveStableKeyFixturePath =
        "Fixtures/runtime-plan-compilation/focused-stable-key/positive/extension-child-collections/fixture.manifest.json";

    private const string MinimalProjectSchemaJson = """
        {
          "projectName": "Ed-Fi",
          "projectVersion": "5.0.0",
          "projectEndpointName": "ed-fi",
          "isExtensionProject": false,
          "description": "Test schema",
          "resourceNameMapping": {},
          "caseInsensitiveEndpointNameMapping": {},
          "educationOrganizationHierarchy": {},
          "educationOrganizationTypes": [],
          "resourceSchemas": {
            "students": {
              "resourceName": "Student",
              "isDescriptor": false,
              "isSchoolYearEnumeration": false,
              "isResourceExtension": false,
              "allowIdentityUpdates": false,
              "isSubclass": false,
              "identityJsonPaths": [
                "$.studentUniqueId"
              ],
              "booleanJsonPaths": [],
              "numericJsonPaths": [],
              "dateJsonPaths": [],
              "dateTimeJsonPaths": [],
              "equalityConstraints": [],
              "arrayUniquenessConstraints": [],
              "documentPathsMapping": {
                "StudentUniqueId": {
                  "isReference": false,
                  "isPartOfIdentity": true,
                  "isRequired": true,
                  "path": "$.studentUniqueId"
                },
                "FirstName": {
                  "isReference": false,
                  "isPartOfIdentity": false,
                  "isRequired": true,
                  "path": "$.firstName"
                }
              },
              "queryFieldMapping": {},
              "securableElements": {
                "Namespace": [],
                "EducationOrganization": [],
                "Student": [],
                "Contact": [],
                "Staff": []
              },
              "authorizationPathways": [],
              "decimalPropertyValidationInfos": [],
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "studentUniqueId": {
                    "type": "string",
                    "maxLength": 32
                  },
                  "firstName": {
                    "type": "string",
                    "maxLength": 75
                  }
                },
                "required": [
                  "studentUniqueId",
                  "firstName"
                ]
              }
            }
          },
          "abstractResources": {}
        }
        """;

    private static readonly string _testHash = new('a', 64);

    private static readonly QualifiedResourceName _studentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    private static EffectiveSchemaSet CreateEffectiveSchemaSet(string? hash = null)
    {
        var effectiveHash = hash ?? _testHash;
        var resourceKeyEntry = new ResourceKeyEntry(1, _studentResource, "5.0.0", false);

        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: effectiveHash,
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false, new string('b', 64)),
            ],
            ResourceKeysInIdOrder: [resourceKeyEntry]
        );

        var projectSchema = (JsonObject)JsonNode.Parse(MinimalProjectSchemaJson)!;

        return new EffectiveSchemaSet(
            effectiveSchema,
            [new EffectiveProjectSchema("ed-fi", "Ed-Fi", "5.0.0", false, projectSchema)]
        );
    }

    private static RuntimeMappingSetCompiler CreateCompiler(
        SqlDialect dialect,
        ISqlDialectRules? dialectRules = null,
        Func<EffectiveSchemaSet>? accessor = null
    )
    {
        var schemaSet = CreateEffectiveSchemaSet();
        return new RuntimeMappingSetCompiler(
            accessor ?? (() => schemaSet),
            new MappingSetCompiler(),
            dialect,
            dialectRules ?? (dialect == SqlDialect.Pgsql ? new PgsqlDialectRules() : new MssqlDialectRules())
        );
    }

    [TestFixture]
    public class Given_Pgsql_Dialect : Given_RuntimeMappingSetCompiler
    {
        [Test]
        public void It_returns_pgsql_dialect()
        {
            var compiler = CreateCompiler(SqlDialect.Pgsql);
            compiler.Dialect.Should().Be(SqlDialect.Pgsql);
        }

        [Test]
        public void It_returns_current_key_matching_effective_schema()
        {
            var compiler = CreateCompiler(SqlDialect.Pgsql);
            var key = compiler.GetCurrentKey();

            key.EffectiveSchemaHash.Should().Be(_testHash);
            key.Dialect.Should().Be(SqlDialect.Pgsql);
            key.RelationalMappingVersion.Should().Be("v1");
        }

        [Test]
        public async Task It_compiles_successfully()
        {
            var compiler = CreateCompiler(SqlDialect.Pgsql);
            var key = compiler.GetCurrentKey();

            var mappingSet = await compiler.CompileAsync(key, CancellationToken.None);

            mappingSet.Should().NotBeNull();
            mappingSet.Key.Should().Be(key);
            mappingSet.Key.Dialect.Should().Be(SqlDialect.Pgsql);
        }
    }

    [TestFixture]
    public class Given_Mssql_Dialect : Given_RuntimeMappingSetCompiler
    {
        [Test]
        public void It_returns_mssql_dialect()
        {
            var compiler = CreateCompiler(SqlDialect.Mssql);
            compiler.Dialect.Should().Be(SqlDialect.Mssql);
        }

        [Test]
        public async Task It_compiles_successfully()
        {
            var compiler = CreateCompiler(SqlDialect.Mssql);
            var key = compiler.GetCurrentKey();

            var mappingSet = await compiler.CompileAsync(key, CancellationToken.None);

            mappingSet.Should().NotBeNull();
            mappingSet.Key.Should().Be(key);
            mappingSet.Key.Dialect.Should().Be(SqlDialect.Mssql);
        }
    }

    [TestFixture]
    public class Given_Key_Mismatch : Given_RuntimeMappingSetCompiler
    {
        [Test]
        public async Task It_throws_InvalidOperationException()
        {
            var compiler = CreateCompiler(SqlDialect.Pgsql);

            var wrongKey = new MappingSetKey(new string('f', 64), SqlDialect.Pgsql, "v1");

            var act = () => compiler.CompileAsync(wrongKey, CancellationToken.None);

            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*Cannot compile*current schema resolved to*");
        }
    }

    [TestFixture]
    public class Given_Accessor_Throws : Given_RuntimeMappingSetCompiler
    {
        [Test]
        public void It_wraps_exception_with_initialization_guidance()
        {
            Func<EffectiveSchemaSet> failingAccessor = () =>
                throw new InvalidOperationException("Schema not initialized");

            var compiler = CreateCompiler(SqlDialect.Pgsql, accessor: failingAccessor);

            var act = () => compiler.GetCurrentKey();

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*runtime mapping initialization failed*")
                .WithInnerException<InvalidOperationException>();
        }
    }

    [TestFixture(SqlDialect.Pgsql)]
    [TestFixture(SqlDialect.Mssql)]
    public class Given_A_Runtime_Collection_Fixture_Missing_Semantic_Identity(SqlDialect dialect)
        : Given_RuntimeMappingSetCompiler
    {
        private Func<Task<MappingSet>> _compile = null!;

        [SetUp]
        public void Setup()
        {
            var schemaSet = RuntimePlanFixtureModelSetBuilder.CreateEffectiveSchemaSet(
                MissingSemanticIdentityFixturePath
            );
            var compiler = CreateCompiler(dialect, accessor: () => schemaSet);
            var key = compiler.GetCurrentKey();

            _compile = () => compiler.CompileAsync(key, CancellationToken.None);
        }

        [Test]
        public async Task It_should_fail_with_the_missing_semantic_identity_diagnostic()
        {
            var exception = (await _compile.Should().ThrowAsync<InvalidOperationException>()).Which;

            exception.Message.Should().Contain("Persisted multi-item scope");
            exception.Message.Should().Contain("$.addresses[*]");
            exception.Message.Should().Contain("Ed-Fi:School");
            exception.Message.Should().Contain("arrayUniquenessConstraints");
        }
    }

    [TestFixture(SqlDialect.Pgsql)]
    [TestFixture(SqlDialect.Mssql)]
    public class Given_A_Runtime_Collection_Fixture_With_Stable_Keys(SqlDialect dialect)
        : Given_RuntimeMappingSetCompiler
    {
        private ResourceWritePlan _writePlan = null!;

        [SetUp]
        public async Task Setup()
        {
            var schemaSet = RuntimePlanFixtureModelSetBuilder.CreateEffectiveSchemaSet(
                PositiveStableKeyFixturePath
            );
            var compiler = CreateCompiler(dialect, accessor: () => schemaSet);
            var key = compiler.GetCurrentKey();
            var mappingSet = await compiler.CompileAsync(key, CancellationToken.None);

            _writePlan = mappingSet.WritePlansByResource[_schoolResource];
        }

        [Test]
        public void It_should_compile_collection_merge_plans_for_true_collection_tables()
        {
            AssertCollectionMergePlan("SchoolAddress", [("$.city", "City")]);
            AssertCollectionMergePlan("SchoolAddressPeriod", [("$.periodName", "PeriodName")]);
            AssertCollectionMergePlan(
                "SchoolExtensionIntervention",
                [("$.interventionCode", "InterventionCode")]
            );
            AssertCollectionMergePlan("SchoolExtensionInterventionVisit", [("$.visitCode", "VisitCode")]);
            AssertCollectionMergePlan(
                "SchoolExtensionAddressSponsorReference",
                [("$.programReference.programName", "Program_DocumentId")]
            );
        }

        [Test]
        public void It_should_keep_non_collection_scopes_on_the_existing_one_to_one_contract()
        {
            var rootPlan = GetTablePlan("School");
            rootPlan.DeleteByParentSql.Should().BeNull();
            rootPlan.CollectionMergePlan.Should().BeNull();

            var rootExtensionPlan = GetTablePlan("SchoolExtension");
            rootExtensionPlan.UpdateSql.Should().NotBeNullOrWhiteSpace();
            rootExtensionPlan.DeleteByParentSql.Should().NotBeNullOrWhiteSpace();
            rootExtensionPlan.CollectionMergePlan.Should().BeNull();

            var collectionExtensionScopePlan = GetTablePlan("SchoolExtensionAddress");
            collectionExtensionScopePlan.UpdateSql.Should().NotBeNullOrWhiteSpace();
            collectionExtensionScopePlan.DeleteByParentSql.Should().NotBeNullOrWhiteSpace();
            collectionExtensionScopePlan.CollectionMergePlan.Should().BeNull();
        }

        private void AssertCollectionMergePlan(
            string tableName,
            IReadOnlyList<(string RelativePath, string ColumnName)> expectedSemanticIdentityBindings
        )
        {
            var tablePlan = GetTablePlan(tableName);

            tablePlan.UpdateSql.Should().BeNull();
            tablePlan.DeleteByParentSql.Should().BeNull();
            tablePlan.CollectionMergePlan.Should().NotBeNull();
            tablePlan.CollectionKeyPreallocationPlan.Should().NotBeNull();

            var collectionMergePlan = tablePlan.CollectionMergePlan!;

            collectionMergePlan.UpdateByStableRowIdentitySql.Should().NotBeNullOrWhiteSpace();
            collectionMergePlan.DeleteByStableRowIdentitySql.Should().NotBeNullOrWhiteSpace();
            collectionMergePlan.CompareBindingIndexesInOrder.Should().NotBeEmpty();
            collectionMergePlan
                .SemanticIdentityBindings.Select(binding =>
                    (
                        binding.RelativePath.Canonical,
                        tablePlan.ColumnBindings[binding.BindingIndex].Column.ColumnName.Value
                    )
                )
                .Should()
                .Equal(expectedSemanticIdentityBindings);

            tablePlan
                .ColumnBindings[collectionMergePlan.StableRowIdentityBindingIndex]
                .Column.ColumnName.Value.Should()
                .Be("CollectionItemId");
            tablePlan
                .ColumnBindings[collectionMergePlan.OrdinalBindingIndex]
                .Column.ColumnName.Value.Should()
                .Be("Ordinal");
        }

        private TableWritePlan GetTablePlan(string tableName)
        {
            return _writePlan.TablePlansInDependencyOrder.Single(tablePlan =>
                string.Equals(tablePlan.TableModel.Table.Name, tableName, StringComparison.Ordinal)
            );
        }
    }
}

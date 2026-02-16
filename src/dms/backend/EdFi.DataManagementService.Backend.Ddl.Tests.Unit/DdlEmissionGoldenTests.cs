// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Base class for DDL emission golden file tests.
/// </summary>
public abstract class DdlEmissionGoldenTestBase
{
    /// <summary>
    /// Find project root by looking for the .csproj file.
    /// </summary>
    protected static string FindProjectRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj"
            );
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj in parent directories."
        );
    }

    /// <summary>
    /// Run git diff between expected and actual files.
    /// </summary>
    protected static string RunGitDiff(string expectedPath, string actualPath)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add("--no-index");
        startInfo.ArgumentList.Add("--ignore-space-at-eol");
        startInfo.ArgumentList.Add("--ignore-cr-at-eol");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(expectedPath);
        startInfo.ArgumentList.Add(actualPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        process.WaitForExit();

        if (process.ExitCode == 0)
        {
            return string.Empty;
        }

        if (process.ExitCode == 1)
        {
            return output;
        }

        return string.IsNullOrWhiteSpace(error) ? output : $"{error}\n{output}".Trim();
    }

    /// <summary>
    /// Check if UPDATE_GOLDENS environment variable is set.
    /// </summary>
    protected static bool ShouldUpdateGoldens()
    {
        var update = Environment.GetEnvironmentVariable("UPDATE_GOLDENS");

        return string.Equals(update, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(update, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Emits DDL and writes the actual output file. Call this in SetUp (arrange + act).
    /// </summary>
    protected static GoldenTestPaths EmitDdl(
        string fixtureName,
        SqlDialect dialect,
        DerivedRelationalModelSet modelSet
    )
    {
        var projectRoot = FindProjectRoot(TestContext.CurrentContext.TestDirectory);
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "ddl-emission");
        var dialectName = dialect == SqlDialect.Pgsql ? "pgsql" : "mssql";
        var expectedPath = Path.Combine(fixtureRoot, "expected", dialectName, $"{fixtureName}.sql");
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "ddl-emission",
            dialectName,
            $"{fixtureName}.sql"
        );

        var dialectRules =
            dialect == SqlDialect.Pgsql ? (ISqlDialectRules)new PgsqlDialectRules() : new MssqlDialectRules();
        var emitter = new RelationalModelDdlEmitter(dialectRules);
        var ddl = emitter.Emit(modelSet);

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, ddl);

        if (ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, ddl);
        }

        return new GoldenTestPaths(expectedPath, actualPath);
    }

    /// <summary>
    /// Asserts that the emitted DDL matches the golden file. Call this in the test method.
    /// </summary>
    protected static void AssertGoldenMatch(GoldenTestPaths paths)
    {
        File.Exists(paths.ExpectedPath)
            .Should()
            .BeTrue($"Golden file missing at {paths.ExpectedPath}. Set UPDATE_GOLDENS=1 to generate.");

        var diffOutput = RunGitDiff(paths.ExpectedPath, paths.ActualPath);

        if (!string.IsNullOrWhiteSpace(diffOutput))
        {
            Assert.Fail(
                $"DDL output does not match golden file.\n\n"
                    + $"Expected: {paths.ExpectedPath}\n"
                    + $"Actual: {paths.ActualPath}\n\n"
                    + $"Diff:\n{diffOutput}"
            );
        }
    }

    protected record GoldenTestPaths(string ExpectedPath, string ActualPath);
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - Nested Collections
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_NestedCollections_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = NestedCollectionsFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("nested-collections", SqlDialect.Pgsql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlEmitter_With_NestedCollections_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = NestedCollectionsFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("nested-collections", SqlDialect.Mssql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - Polymorphic Abstract Views
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_PolymorphicAbstract_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = PolymorphicAbstractFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("polymorphic-abstract", SqlDialect.Pgsql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlEmitter_With_PolymorphicAbstract_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = PolymorphicAbstractFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("polymorphic-abstract", SqlDialect.Mssql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - Extension Mapping
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_ExtensionMapping_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = ExtensionMappingFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("extension-mapping", SqlDialect.Pgsql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlEmitter_With_ExtensionMapping_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = ExtensionMappingFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("extension-mapping", SqlDialect.Mssql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Fixture Builders
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Fixture for nested collections scenario:
/// School → SchoolAddress → SchoolAddressPhoneNumber
/// </summary>
internal static class NestedCollectionsFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        // Column names
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var addressOrdinalColumn = new DbColumnName("AddressOrdinal");
        var streetColumn = new DbColumnName("Street");
        var phoneOrdinalColumn = new DbColumnName("PhoneNumberOrdinal");
        var phoneNumberColumn = new DbColumnName("PhoneNumber");

        // Root table: School
        var schoolTableName = new DbTableName(schema, "School");
        var schoolTable = new DbTableModel(
            schoolTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // Child collection: SchoolAddress
        var addressTableName = new DbTableName(schema, "SchoolAddress");
        var addressTable = new DbTableModel(
            addressTableName,
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                "PK_SchoolAddress",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(addressOrdinalColumn, ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    addressOrdinalColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    streetColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 100),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolAddress_School",
                    [documentIdColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // Nested collection: SchoolAddressPhoneNumber
        var phoneTableName = new DbTableName(schema, "SchoolAddressPhoneNumber");
        var phoneTable = new DbTableModel(
            phoneTableName,
            new JsonPathExpression("$.addresses[*].phoneNumbers[*]", []),
            new TableKey(
                "PK_SchoolAddressPhoneNumber",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(addressOrdinalColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(phoneOrdinalColumn, ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    addressOrdinalColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    phoneOrdinalColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    phoneNumberColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 20),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolAddressPhoneNumber_SchoolAddress",
                    [documentIdColumn, addressOrdinalColumn],
                    addressTableName,
                    [documentIdColumn, addressOrdinalColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable, addressTable, phoneTable],
            [],
            []
        );

        // Triggers
        var triggers = new List<DbTriggerInfo>
        {
            // DocumentStamping on root table (with identity projection column SchoolId)
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                DbTriggerKind.DocumentStamping,
                [documentIdColumn],
                [schoolIdColumn]
            ),
            // DocumentStamping on child table SchoolAddress (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddress_Stamp"),
                addressTableName,
                DbTriggerKind.DocumentStamping,
                [documentIdColumn],
                []
            ),
            // DocumentStamping on nested child table (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddressPhoneNumber_Stamp"),
                phoneTableName,
                DbTriggerKind.DocumentStamping,
                [documentIdColumn],
                []
            ),
            // ReferentialIdentityMaintenance on root table
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                DbTriggerKind.ReferentialIdentityMaintenance,
                [documentIdColumn],
                [schoolIdColumn],
                ResourceKeyId: 1,
                ProjectName: "Ed-Fi",
                ResourceName: "School",
                IdentityElements: [new IdentityElementMapping(schoolIdColumn, "$.schoolId")]
            ),
        };

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                1,
                [0x01],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [],
            triggers
        );
    }
}

/// <summary>
/// Fixture for polymorphic abstract views scenario:
/// EducationOrganization (abstract) with School + LEA concrete types
/// </summary>
internal static class PolymorphicAbstractFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");
        var discriminatorColumn = new DbColumnName("Discriminator");
        var organizationIdColumn = new DbColumnName("EducationOrganizationId");

        // Abstract resource
        var abstractResource = new QualifiedResourceName("Ed-Fi", "EducationOrganization");
        var abstractResourceKey = new ResourceKeyEntry(1, abstractResource, "1.0.0", true);

        // Concrete resources
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var schoolResourceKey = new ResourceKeyEntry(2, schoolResource, "1.0.0", false);

        var leaResource = new QualifiedResourceName("Ed-Fi", "LocalEducationAgency");
        var leaResourceKey = new ResourceKeyEntry(3, leaResource, "1.0.0", false);

        // Identity table for abstract type
        var identityTableName = new DbTableName(schema, "EducationOrganizationIdentity");
        var identityTable = new DbTableModel(
            identityTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_EducationOrganizationIdentity",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    organizationIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    discriminatorColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // School concrete table
        var schoolTableName = new DbTableName(schema, "School");
        var schoolTable = new DbTableModel(
            schoolTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    organizationIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_School_EducationOrganizationIdentity",
                    [documentIdColumn],
                    identityTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // LEA concrete table
        var leaTableName = new DbTableName(schema, "LocalEducationAgency");
        var leaTable = new DbTableModel(
            leaTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_LocalEducationAgency",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    organizationIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_LocalEducationAgency_EducationOrganizationIdentity",
                    [documentIdColumn],
                    identityTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // Abstract union view
        var viewName = new DbTableName(schema, "EducationOrganization");
        var outputColumns = new List<AbstractUnionViewOutputColumn>
        {
            new(documentIdColumn, new RelationalScalarType(ScalarKind.Int64), null, null),
            new(organizationIdColumn, new RelationalScalarType(ScalarKind.Int32), null, null),
            new(discriminatorColumn, new RelationalScalarType(ScalarKind.String, MaxLength: 50), null, null),
        };

        var schoolArm = new AbstractUnionViewArm(
            schoolResourceKey,
            schoolTableName,
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                new AbstractUnionViewProjectionExpression.SourceColumn(organizationIdColumn),
                new AbstractUnionViewProjectionExpression.StringLiteral("School"),
            ]
        );

        var leaArm = new AbstractUnionViewArm(
            leaResourceKey,
            leaTableName,
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                new AbstractUnionViewProjectionExpression.SourceColumn(organizationIdColumn),
                new AbstractUnionViewProjectionExpression.StringLiteral("LocalEducationAgency"),
            ]
        );

        var unionView = new AbstractUnionViewInfo(
            abstractResourceKey,
            viewName,
            outputColumns,
            [schoolArm, leaArm]
        );

        var abstractIdentityTable = new AbstractIdentityTableInfo(abstractResourceKey, identityTable);

        var schoolRelationalModel = new RelationalResourceModel(
            schoolResource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable],
            [],
            []
        );

        var leaRelationalModel = new RelationalResourceModel(
            leaResource,
            schema,
            ResourceStorageKind.RelationalTables,
            leaTable,
            [leaTable],
            [],
            []
        );

        // Triggers
        var superclassAlias = new SuperclassAliasInfo(
            1,
            "Ed-Fi",
            "EducationOrganization",
            [new IdentityElementMapping(organizationIdColumn, "$.educationOrganizationId")]
        );

        var triggers = new List<DbTriggerInfo>
        {
            // DocumentStamping on LEA root (with identity projection)
            new(
                new DbTriggerName("TR_LocalEducationAgency_Stamp"),
                leaTableName,
                DbTriggerKind.DocumentStamping,
                [documentIdColumn],
                [organizationIdColumn]
            ),
            // AbstractIdentityMaintenance on LEA → EducationOrganizationIdentity
            new(
                new DbTriggerName("TR_LocalEducationAgency_AbstractIdentity"),
                leaTableName,
                DbTriggerKind.AbstractIdentityMaintenance,
                [documentIdColumn],
                [organizationIdColumn],
                identityTableName,
                TargetColumnMappings: [new TriggerColumnMapping(organizationIdColumn, organizationIdColumn)],
                DiscriminatorValue: "Ed-Fi:LocalEducationAgency"
            ),
            // ReferentialIdentityMaintenance on LEA
            new(
                new DbTriggerName("TR_LocalEducationAgency_ReferentialIdentity"),
                leaTableName,
                DbTriggerKind.ReferentialIdentityMaintenance,
                [documentIdColumn],
                [organizationIdColumn],
                ResourceKeyId: 3,
                ProjectName: "Ed-Fi",
                ResourceName: "LocalEducationAgency",
                IdentityElements:
                [
                    new IdentityElementMapping(organizationIdColumn, "$.educationOrganizationId"),
                ],
                SuperclassAlias: superclassAlias
            ),
            // DocumentStamping on School root (with identity projection)
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                DbTriggerKind.DocumentStamping,
                [documentIdColumn],
                [organizationIdColumn]
            ),
            // AbstractIdentityMaintenance on School → EducationOrganizationIdentity
            new(
                new DbTriggerName("TR_School_AbstractIdentity"),
                schoolTableName,
                DbTriggerKind.AbstractIdentityMaintenance,
                [documentIdColumn],
                [organizationIdColumn],
                identityTableName,
                TargetColumnMappings: [new TriggerColumnMapping(organizationIdColumn, organizationIdColumn)],
                DiscriminatorValue: "Ed-Fi:School"
            ),
            // ReferentialIdentityMaintenance on School
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                DbTriggerKind.ReferentialIdentityMaintenance,
                [documentIdColumn],
                [organizationIdColumn],
                ResourceKeyId: 2,
                ProjectName: "Ed-Fi",
                ResourceName: "School",
                IdentityElements:
                [
                    new IdentityElementMapping(organizationIdColumn, "$.educationOrganizationId"),
                ],
                SuperclassAlias: superclassAlias
            ),
        };

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                3,
                [0x01, 0x02, 0x03],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [abstractResourceKey, schoolResourceKey, leaResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [
                new ConcreteResourceModel(
                    schoolResourceKey,
                    ResourceStorageKind.RelationalTables,
                    schoolRelationalModel
                ),
                new ConcreteResourceModel(
                    leaResourceKey,
                    ResourceStorageKind.RelationalTables,
                    leaRelationalModel
                ),
            ],
            [abstractIdentityTable],
            [unionView],
            [],
            triggers
        );
    }
}

/// <summary>
/// Fixture for extension mapping scenario:
/// School (core) with Sample extension at root and nested collection levels
/// </summary>
internal static class ExtensionMappingFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var edfiSchema = new DbSchemaName("edfi");
        var sampleSchema = new DbSchemaName("sample");
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        // Column names
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var addressOrdinalColumn = new DbColumnName("AddressOrdinal");
        var streetColumn = new DbColumnName("Street");
        var extensionDataColumn = new DbColumnName("ExtensionData");
        var addressExtDataColumn = new DbColumnName("AddressExtensionData");

        // Core table: School
        var schoolTableName = new DbTableName(edfiSchema, "School");
        var schoolTable = new DbTableModel(
            schoolTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // Core collection: SchoolAddress
        var addressTableName = new DbTableName(edfiSchema, "SchoolAddress");
        var addressTable = new DbTableModel(
            addressTableName,
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                "PK_SchoolAddress",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(addressOrdinalColumn, ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    addressOrdinalColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    streetColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 100),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolAddress_School",
                    [documentIdColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // Root extension: SchoolExtension
        var schoolExtTableName = new DbTableName(sampleSchema, "SchoolExtension");
        var schoolExtTable = new DbTableModel(
            schoolExtTableName,
            new JsonPathExpression("$._ext.sample", []),
            new TableKey("PK_SchoolExtension", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    extensionDataColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 200),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolExtension_School",
                    [documentIdColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // Nested extension: SchoolAddressExtension
        var addressExtTableName = new DbTableName(sampleSchema, "SchoolAddressExtension");
        var addressExtTable = new DbTableModel(
            addressExtTableName,
            new JsonPathExpression("$.addresses[*]._ext.sample", []),
            new TableKey(
                "PK_SchoolAddressExtension",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(addressOrdinalColumn, ColumnKind.ParentKeyPart),
                ]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    addressOrdinalColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    addressExtDataColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 100),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolAddressExtension_SchoolAddress",
                    [documentIdColumn, addressOrdinalColumn],
                    addressTableName,
                    [documentIdColumn, addressOrdinalColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            edfiSchema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable, addressTable, schoolExtTable, addressExtTable],
            [],
            []
        );

        // Triggers
        var triggers = new List<DbTriggerInfo>
        {
            // DocumentStamping on root table (with identity projection column SchoolId)
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                DbTriggerKind.DocumentStamping,
                [documentIdColumn],
                [schoolIdColumn]
            ),
            // DocumentStamping on child table SchoolAddress (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddress_Stamp"),
                addressTableName,
                DbTriggerKind.DocumentStamping,
                [documentIdColumn],
                []
            ),
            // DocumentStamping on extension table SchoolAddressExtension (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddressExtension_Stamp"),
                addressExtTableName,
                DbTriggerKind.DocumentStamping,
                [documentIdColumn],
                []
            ),
            // DocumentStamping on extension table SchoolExtension (no identity projection)
            new(
                new DbTriggerName("TR_SchoolExtension_Stamp"),
                schoolExtTableName,
                DbTriggerKind.DocumentStamping,
                [documentIdColumn],
                []
            ),
            // ReferentialIdentityMaintenance on root table
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                DbTriggerKind.ReferentialIdentityMaintenance,
                [documentIdColumn],
                [schoolIdColumn],
                ResourceKeyId: 1,
                ProjectName: "Ed-Fi",
                ResourceName: "School",
                IdentityElements: [new IdentityElementMapping(schoolIdColumn, "$.schoolId")]
            ),
        };

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                1,
                [0x01],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                    new SchemaComponentInfo(
                        "sample",
                        "Sample",
                        "1.0.0",
                        false,
                        "aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, edfiSchema),
                new ProjectSchemaInfo("sample", "Sample", "1.0.0", false, sampleSchema),
            ],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [],
            triggers
        );
    }
}

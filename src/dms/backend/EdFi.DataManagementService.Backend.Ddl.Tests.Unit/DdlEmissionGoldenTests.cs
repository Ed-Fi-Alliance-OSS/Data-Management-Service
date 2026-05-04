// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Base class for DDL emission golden file tests.
/// </summary>
public abstract class DdlEmissionGoldenTestBase
{
    private const string _csprojFileName = "EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj";

    protected static string FindProjectRoot(string startDirectory) =>
        GoldenFixtureTestHelpers.FindProjectRoot(startDirectory, _csprojFileName);

    protected static string RunGitDiff(string expectedPath, string actualPath) =>
        GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);

    protected static bool ShouldUpdateGoldens() => GoldenFixtureTestHelpers.ShouldUpdateGoldens();

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
        var dialectName = dialect switch
        {
            SqlDialect.Pgsql => "pgsql",
            SqlDialect.Mssql => "mssql",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };
        var expectedPath = Path.Combine(fixtureRoot, "expected", dialectName, $"{fixtureName}.sql");
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "ddl-emission",
            dialectName,
            $"{fixtureName}.sql"
        );

        var dialectInstance = SqlDialectFactory.Create(dialect);
        var emitter = new RelationalModelDdlEmitter(dialectInstance);
        var ddl = emitter.Emit(modelSet);

        // Strict canonicalization checks on the raw emitter output, independent of
        // the git diff flags (--ignore-space-at-eol, --ignore-cr-at-eol) used below.
        ddl.Should().NotContain("\r", "emitted DDL must use \\n line endings only, never \\r");
        ddl.Split('\n')
            .Where(line => line.Length > 0 && line[^1] == ' ')
            .Should()
            .BeEmpty("emitted DDL must not contain trailing whitespace on any line");

        // MSSQL batch-boundary check: every CREATE OR ALTER TRIGGER must be preceded
        // by a GO batch separator to avoid "must be the first statement in a batch" errors.
        if (dialect == SqlDialect.Mssql)
        {
            AssertMssqlBatchBoundaries(ddl);
        }

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

    /// <summary>
    /// Validates that every <c>CREATE OR ALTER TRIGGER</c> statement in MSSQL DDL output
    /// is preceded by a <c>GO</c> batch separator.
    /// </summary>
    private static void AssertMssqlBatchBoundaries(string ddl)
    {
        var lines = ddl.Split('\n');
        var violations = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("CREATE OR ALTER TRIGGER", StringComparison.Ordinal))
            {
                continue;
            }

            // Walk backwards to find the most recent non-blank line
            var foundGo = false;
            for (var j = i - 1; j >= 0; j--)
            {
                var prev = lines[j].Trim();
                if (prev.Length == 0)
                {
                    continue;
                }

                foundGo = string.Equals(prev, "GO", StringComparison.OrdinalIgnoreCase);
                break;
            }

            if (!foundGo)
            {
                violations.Add(
                    $"Line {i + 1}: '{trimmed[..Math.Min(60, trimmed.Length)]}...' is not preceded by GO"
                );
            }
        }

        violations
            .Should()
            .BeEmpty("every CREATE OR ALTER TRIGGER in MSSQL DDL must be preceded by a GO batch separator");
    }

    /// <summary>
    /// Emits a DDL manifest (both dialects) and writes the actual output file.
    /// Builds model sets for both dialects, combines core + relational + seed DDL,
    /// then calls <see cref="DdlManifestEmitter.Emit"/> to produce the manifest JSON.
    /// </summary>
    /// <remarks>
    /// The golden fixtures use deterministic, contract-valid synthetic fingerprint values generated
    /// from a stable fixture key. This keeps full DDL emission coverage aligned with the runtime
    /// provisioning contract without coupling snapshot tests to production fingerprint generation logic.
    /// </remarks>
    protected static GoldenTestPaths EmitDdlManifest(
        string fixtureName,
        Func<SqlDialect, DerivedRelationalModelSet> buildModelSet
    )
    {
        var projectRoot = FindProjectRoot(TestContext.CurrentContext.TestDirectory);
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "ddl-emission");
        var expectedPath = Path.Combine(fixtureRoot, "expected", "ddl-manifest", $"{fixtureName}.json");
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "ddl-emission",
            "ddl-manifest",
            $"{fixtureName}.json"
        );

        SqlDialect[] dialects = [SqlDialect.Pgsql, SqlDialect.Mssql];
        var entries = new List<DdlManifestEntry>();
        EffectiveSchemaInfo? effectiveSchema = null;

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        foreach (var dialect in dialects)
        {
            var modelSet = buildModelSet(dialect);
            effectiveSchema ??= modelSet.EffectiveSchema;

            var sqlDialect = SqlDialectFactory.Create(dialect);
            var combinedSql = FullDdlEmitter.Emit(sqlDialect, modelSet);

            entries.Add(new DdlManifestEntry(dialect, combinedSql));
        }

        var manifest = DdlManifestEmitter.Emit(effectiveSchema!, entries);

        manifest.Should().NotContain("\r", "manifest must use \\n line endings only");
        manifest.Should().EndWith("\n", "manifest must end with trailing newline");

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, manifest, utf8NoBom);

        if (ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest, utf8NoBom);
        }

        return new GoldenTestPaths(expectedPath, actualPath);
    }

    protected record GoldenTestPaths(string ExpectedPath, string ActualPath);
}

internal static class GoldenEffectiveSchemaFixtureData
{
    internal static EffectiveSchemaInfo Create(
        string fixtureKey,
        IReadOnlyList<SchemaComponentInfo> schemaComponentsInEndpointOrder,
        IReadOnlyList<ResourceKeyEntry> resourceKeysInIdOrder
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureKey);
        ArgumentNullException.ThrowIfNull(schemaComponentsInEndpointOrder);
        ArgumentNullException.ThrowIfNull(resourceKeysInIdOrder);

        return new EffectiveSchemaInfo(
            "1.0.0",
            "1.0.0",
            CreateEffectiveSchemaHash(fixtureKey),
            EffectiveSchemaFingerprintContract.CreateResourceKeyCountOrThrow(resourceKeysInIdOrder.Count),
            CreateResourceKeySeedHash(fixtureKey),
            schemaComponentsInEndpointOrder,
            resourceKeysInIdOrder
        );
    }

    private static string CreateEffectiveSchemaHash(string fixtureKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{fixtureKey}:effective-schema")));

    private static byte[] CreateResourceKeySeedHash(string fixtureKey) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{fixtureKey}:resource-key-seed"));
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
// Golden File Tests - Identity Propagation
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_IdentityPropagation_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = IdentityPropagationFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("identity-propagation", SqlDialect.Pgsql, modelSet);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlEmitter_With_IdentityPropagation_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = IdentityPropagationFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("identity-propagation", SqlDialect.Mssql, modelSet);
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
// Golden File Tests - Key Unification
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_KeyUnification_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = KeyUnificationFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("key-unification", SqlDialect.Pgsql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_stored_generated_column_for_alias()
    {
        _ddlContent.Should().Contain("GENERATED ALWAYS AS", "alias columns should emit GENERATED ALWAYS AS");
        _ddlContent.Should().Contain("STORED", "alias columns should be STORED in PostgreSQL");
    }
}

[TestFixture]
public class Given_DdlEmitter_With_KeyUnification_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = KeyUnificationFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("key-unification", SqlDialect.Mssql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_persisted_computed_column_for_alias()
    {
        _ddlContent.Should().Contain("PERSISTED", "alias columns should be PERSISTED in MSSQL");
    }

    [Test]
    public void It_should_not_use_update_on_alias_columns()
    {
        // Alias columns are computed — UPDATE() on them would fail with Msg 2114
        _ddlContent
            .Should()
            .NotContain("UPDATE([CourseOffering_SchoolId])", "UPDATE() must not reference alias columns");
        _ddlContent
            .Should()
            .NotContain("UPDATE([School_SchoolId])", "UPDATE() must not reference alias columns");
    }

    [Test]
    public void It_should_not_set_alias_columns_in_propagation()
    {
        // Alias columns are computed — SET on them would fail with Msg 271
        _ddlContent
            .Should()
            .NotContain("SET r.[CourseOffering_SchoolId]", "SET must not target alias columns");
        _ddlContent.Should().NotContain("SET r.[School_SchoolId]", "SET must not target alias columns");
    }

    [Test]
    public void It_should_use_canonical_column_in_update_guard()
    {
        _ddlContent
            .Should()
            .Contain("UPDATE([SchoolId_Unified])", "UPDATE() should reference canonical stored column");
    }

    [Test]
    public void It_should_use_canonical_column_in_propagation_set()
    {
        _ddlContent
            .Should()
            .Contain("[SchoolId_Unified]", "propagation SET should reference canonical stored column");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - FK Support Indexes
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_FkSupportIndex_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = FkSupportIndexFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("fk-support-index", SqlDialect.Pgsql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_fk_support_index_exactly_once()
    {
        // Use dialect-specific quoted identifier pattern for precise matching
        // PostgreSQL index names are NOT schema-qualified in CREATE INDEX
        var indexMatches = System.Text.RegularExpressions.Regex.Matches(
            _ddlContent,
            @"CREATE\s+INDEX\s+IF\s+NOT\s+EXISTS\s+""IX_Enrollment_SchoolId""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        indexMatches.Count.Should().Be(1, "FK-support index should be emitted exactly once");
    }
}

[TestFixture]
public class Given_DdlEmitter_With_FkSupportIndex_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = FkSupportIndexFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("fk-support-index", SqlDialect.Mssql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_fk_support_index_exactly_once()
    {
        // Use dialect-specific quoted identifier pattern for precise matching
        var indexMatches = System.Text.RegularExpressions.Regex.Matches(
            _ddlContent,
            @"CREATE\s+INDEX\s+\[IX_Enrollment_SchoolId\]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        indexMatches.Count.Should().Be(1, "FK-support index should be emitted exactly once");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - Auth EdOrg Hierarchy
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_AuthEdOrgHierarchy_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = AuthEdOrgHierarchyFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("auth-edorg-hierarchy", SqlDialect.Pgsql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_auth_schema()
    {
        _ddlContent.Should().Contain("auth", "auth schema should be created");
    }

    [Test]
    public void It_should_emit_auth_hierarchy_table()
    {
        _ddlContent
            .Should()
            .Contain(
                "EducationOrganizationIdToEducationOrganizationId",
                "auth hierarchy table should be created"
            );
    }

    [Test]
    public void It_should_emit_auth_covering_index()
    {
        _ddlContent
            .Should()
            .Contain(
                "IX_EducationOrganizationIdToEducationOrganizationId_Target",
                "auth covering index should be created"
            );
    }

    [Test]
    public void It_should_emit_auth_triggers_for_leaf_entity()
    {
        _ddlContent
            .Should()
            .Contain(
                "TR_StateEducationAgency_AuthHierarchy_Insert",
                "INSERT trigger for leaf entity should be created"
            );
        _ddlContent
            .Should()
            .Contain(
                "TR_StateEducationAgency_AuthHierarchy_Delete",
                "DELETE trigger for leaf entity should be created"
            );
    }

    [Test]
    public void It_should_emit_auth_triggers_for_hierarchical_entity()
    {
        _ddlContent
            .Should()
            .Contain(
                "TR_LocalEducationAgency_AuthHierarchy_Insert",
                "INSERT trigger for hierarchical entity should be created"
            );
        _ddlContent
            .Should()
            .Contain(
                "TR_LocalEducationAgency_AuthHierarchy_Update",
                "UPDATE trigger for hierarchical entity should be created"
            );
        _ddlContent
            .Should()
            .Contain(
                "TR_LocalEducationAgency_AuthHierarchy_Delete",
                "DELETE trigger for hierarchical entity should be created"
            );
    }
}

[TestFixture]
public class Given_DdlEmitter_With_AuthEdOrgHierarchy_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = AuthEdOrgHierarchyFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("auth-edorg-hierarchy", SqlDialect.Mssql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_auth_schema()
    {
        _ddlContent.Should().Contain("auth", "auth schema should be created");
    }

    [Test]
    public void It_should_emit_auth_hierarchy_table()
    {
        _ddlContent
            .Should()
            .Contain(
                "EducationOrganizationIdToEducationOrganizationId",
                "auth hierarchy table should be created"
            );
    }

    [Test]
    public void It_should_emit_auth_covering_index()
    {
        _ddlContent
            .Should()
            .Contain(
                "IX_EducationOrganizationIdToEducationOrganizationId_Target",
                "auth covering index should be created"
            );
    }

    [Test]
    public void It_should_emit_auth_triggers_for_leaf_entity()
    {
        _ddlContent
            .Should()
            .Contain(
                "TR_StateEducationAgency_AuthHierarchy_Insert",
                "INSERT trigger for leaf entity should be created"
            );
        _ddlContent
            .Should()
            .Contain(
                "TR_StateEducationAgency_AuthHierarchy_Delete",
                "DELETE trigger for leaf entity should be created"
            );
    }

    [Test]
    public void It_should_emit_auth_triggers_for_hierarchical_entity()
    {
        _ddlContent
            .Should()
            .Contain(
                "TR_LocalEducationAgency_AuthHierarchy_Insert",
                "INSERT trigger for hierarchical entity should be created"
            );
        _ddlContent
            .Should()
            .Contain(
                "TR_LocalEducationAgency_AuthHierarchy_Update",
                "UPDATE trigger for hierarchical entity should be created"
            );
        _ddlContent
            .Should()
            .Contain(
                "TR_LocalEducationAgency_AuthHierarchy_Delete",
                "DELETE trigger for hierarchical entity should be created"
            );
    }
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - DDL Manifest
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlManifest_For_NestedCollections : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        _paths = EmitDdlManifest("nested-collections", NestedCollectionsFixture.Build);
    }

    [Test]
    public void It_should_emit_manifest_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlManifest_For_PolymorphicAbstract : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        _paths = EmitDdlManifest("polymorphic-abstract", PolymorphicAbstractFixture.Build);
    }

    [Test]
    public void It_should_emit_manifest_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlManifest_For_IdentityPropagation : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        _paths = EmitDdlManifest("identity-propagation", IdentityPropagationFixture.Build);
    }

    [Test]
    public void It_should_emit_manifest_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlManifest_For_ExtensionMapping : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        _paths = EmitDdlManifest("extension-mapping", ExtensionMappingFixture.Build);
    }

    [Test]
    public void It_should_emit_manifest_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlManifest_For_KeyUnification : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        _paths = EmitDdlManifest("key-unification", KeyUnificationFixture.Build);
    }

    [Test]
    public void It_should_emit_manifest_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlManifest_For_FkSupportIndex : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        _paths = EmitDdlManifest("fk-support-index", FkSupportIndexFixture.Build);
    }

    [Test]
    public void It_should_emit_manifest_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_DdlManifest_For_AuthEdOrgHierarchy : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        _paths = EmitDdlManifest("auth-edorg-hierarchy", AuthEdOrgHierarchyFixture.Build);
    }

    [Test]
    public void It_should_emit_manifest_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Golden File Tests - People Auth Views
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_With_AuthPeopleViews_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = AuthPeopleViewsFixture.Build(SqlDialect.Pgsql);
        _paths = EmitDdl("auth-people-views", SqlDialect.Pgsql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_student_view()
    {
        _ddlContent
            .Should()
            .Contain(
                "EducationOrganizationIdToStudentDocumentId",
                "Student people auth view should be created"
            );
    }

    [Test]
    public void It_should_emit_contact_view()
    {
        _ddlContent
            .Should()
            .Contain(
                "EducationOrganizationIdToContactDocumentId",
                "Contact people auth view should be created"
            );
    }

    [Test]
    public void It_should_emit_staff_view()
    {
        _ddlContent
            .Should()
            .Contain("EducationOrganizationIdToStaffDocumentId", "Staff people auth view should be created");
    }

    [Test]
    public void It_should_emit_student_through_responsibility_view()
    {
        _ddlContent
            .Should()
            .Contain(
                "EducationOrganizationIdToStudentDocumentIdThroughResponsibility",
                "StudentThroughResponsibility people auth view should be created"
            );
    }

    [Test]
    public void It_should_use_create_or_replace_view()
    {
        _ddlContent.Should().Contain("CREATE OR REPLACE VIEW", "PgSQL should use CREATE OR REPLACE VIEW");
    }

    [Test]
    public void It_should_use_select_distinct()
    {
        _ddlContent.Should().Contain("SELECT DISTINCT", "People auth views should use SELECT DISTINCT");
    }
}

[TestFixture]
public class Given_DdlEmitter_With_AuthPeopleViews_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = AuthPeopleViewsFixture.Build(SqlDialect.Mssql);
        _paths = EmitDdl("auth-people-views", SqlDialect.Mssql, modelSet);
        _ddlContent = File.ReadAllText(_paths.ActualPath);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }

    [Test]
    public void It_should_emit_student_view()
    {
        _ddlContent
            .Should()
            .Contain(
                "EducationOrganizationIdToStudentDocumentId",
                "Student people auth view should be created"
            );
    }

    [Test]
    public void It_should_emit_contact_view()
    {
        _ddlContent
            .Should()
            .Contain(
                "EducationOrganizationIdToContactDocumentId",
                "Contact people auth view should be created"
            );
    }

    [Test]
    public void It_should_emit_staff_view()
    {
        _ddlContent
            .Should()
            .Contain("EducationOrganizationIdToStaffDocumentId", "Staff people auth view should be created");
    }

    [Test]
    public void It_should_emit_student_through_responsibility_view()
    {
        _ddlContent
            .Should()
            .Contain(
                "EducationOrganizationIdToStudentDocumentIdThroughResponsibility",
                "StudentThroughResponsibility people auth view should be created"
            );
    }

    [Test]
    public void It_should_use_create_or_alter_view()
    {
        _ddlContent.Should().Contain("CREATE OR ALTER VIEW", "MSSQL should use CREATE OR ALTER VIEW");
    }

    [Test]
    public void It_should_emit_go_batch_separators()
    {
        _ddlContent.Should().Contain("GO\n", "MSSQL should emit GO batch separators before views");
    }
}

[TestFixture]
public class Given_DdlManifest_For_AuthPeopleViews : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        _paths = EmitDdlManifest("auth-people-views", AuthPeopleViewsFixture.Build);
    }

    [Test]
    public void It_should_emit_manifest_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

// ═══════════════════════════════════════════════════════════════════
// People Auth Views - Negative Test (no auth hierarchy)
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_Without_AuthHierarchy_Should_Not_Emit_PeopleViews : DdlEmissionGoldenTestBase
{
    private string _ddlContent = default!;

    [SetUp]
    public void Setup()
    {
        // NestedCollectionsFixture has no auth hierarchy (defaults to null)
        var modelSet = NestedCollectionsFixture.Build(SqlDialect.Pgsql);
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        _ddlContent = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_not_contain_people_auth_views()
    {
        _ddlContent
            .Should()
            .NotContain(
                "EducationOrganizationIdToStudentDocumentId",
                "People auth views should not be emitted when no auth hierarchy exists"
            );
    }

    [Test]
    public void It_should_not_contain_auth_schema_views()
    {
        _ddlContent
            .Should()
            .NotContain(
                "EducationOrganizationIdToContactDocumentId",
                "Contact auth view should not be emitted without auth hierarchy"
            );
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
        List<DbTriggerInfo> triggers =
        [
            // DocumentStamping on root table (with identity projection column SchoolId)
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // DocumentStamping on child table SchoolAddress (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddress_Stamp"),
                addressTableName,
                [documentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping()
            ),
            // DocumentStamping on nested child table (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddressPhoneNumber_Stamp"),
                phoneTableName,
                [documentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on root table
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "School",
                    [
                        new IdentityElementMapping(
                            schoolIdColumn,
                            "$.schoolId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ]
                )
            ),
        ];

        return new DerivedRelationalModelSet(
            GoldenEffectiveSchemaFixtureData.Create(
                "nested-collections",
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
            [
                // FK from abstract identity table to dms.Document (as created by BuildIdentityTableConstraints)
                new TableConstraint.ForeignKey(
                    "FK_EducationOrganizationIdentity_Document",
                    [documentIdColumn],
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
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
        var viewName = new DbTableName(schema, "EducationOrganization_View");
        List<AbstractUnionViewOutputColumn> outputColumns =
        [
            new(documentIdColumn, new RelationalScalarType(ScalarKind.Int64), null, null),
            new(organizationIdColumn, new RelationalScalarType(ScalarKind.Int32), null, null),
            new(discriminatorColumn, new RelationalScalarType(ScalarKind.String, MaxLength: 50), null, null),
        ];

        var schoolArm = new AbstractUnionViewArm(
            schoolResourceKey,
            schoolTableName,
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                new AbstractUnionViewProjectionExpression.SourceColumn(organizationIdColumn),
                new AbstractUnionViewProjectionExpression.StringLiteral("Ed-Fi:School"),
            ]
        );

        var leaArm = new AbstractUnionViewArm(
            leaResourceKey,
            leaTableName,
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                new AbstractUnionViewProjectionExpression.SourceColumn(organizationIdColumn),
                new AbstractUnionViewProjectionExpression.StringLiteral("Ed-Fi:LocalEducationAgency"),
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
            [
                new IdentityElementMapping(
                    organizationIdColumn,
                    "$.educationOrganizationId",
                    new RelationalScalarType(ScalarKind.Int32)
                ),
            ]
        );

        List<DbTriggerInfo> triggers =
        [
            // DocumentStamping on LEA root (with identity projection)
            new(
                new DbTriggerName("TR_LocalEducationAgency_Stamp"),
                leaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // AbstractIdentityMaintenance on LEA → EducationOrganizationIdentity
            new(
                new DbTriggerName("TR_LocalEducationAgency_AbstractIdentity"),
                leaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.AbstractIdentityMaintenance(
                    identityTableName,
                    [new TriggerColumnMapping(organizationIdColumn, organizationIdColumn)],
                    "Ed-Fi:LocalEducationAgency"
                )
            ),
            // ReferentialIdentityMaintenance on LEA
            new(
                new DbTriggerName("TR_LocalEducationAgency_ReferentialIdentity"),
                leaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    3,
                    "Ed-Fi",
                    "LocalEducationAgency",
                    [
                        new IdentityElementMapping(
                            organizationIdColumn,
                            "$.educationOrganizationId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ],
                    superclassAlias
                )
            ),
            // DocumentStamping on School root (with identity projection)
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // AbstractIdentityMaintenance on School → EducationOrganizationIdentity
            new(
                new DbTriggerName("TR_School_AbstractIdentity"),
                schoolTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.AbstractIdentityMaintenance(
                    identityTableName,
                    [new TriggerColumnMapping(organizationIdColumn, organizationIdColumn)],
                    "Ed-Fi:School"
                )
            ),
            // ReferentialIdentityMaintenance on School
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    2,
                    "Ed-Fi",
                    "School",
                    [
                        new IdentityElementMapping(
                            organizationIdColumn,
                            "$.educationOrganizationId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ],
                    superclassAlias
                )
            ),
        ];

        return new DerivedRelationalModelSet(
            GoldenEffectiveSchemaFixtureData.Create(
                "polymorphic-abstract",
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
        List<DbTriggerInfo> triggers =
        [
            // DocumentStamping on root table (with identity projection column SchoolId)
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // DocumentStamping on child table SchoolAddress (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddress_Stamp"),
                addressTableName,
                [documentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping()
            ),
            // DocumentStamping on extension table SchoolAddressExtension (no identity projection)
            new(
                new DbTriggerName("TR_SchoolAddressExtension_Stamp"),
                addressExtTableName,
                [documentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping()
            ),
            // DocumentStamping on extension table SchoolExtension (no identity projection)
            new(
                new DbTriggerName("TR_SchoolExtension_Stamp"),
                schoolExtTableName,
                [documentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on root table
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "School",
                    [
                        new IdentityElementMapping(
                            schoolIdColumn,
                            "$.schoolId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ]
                )
            ),
        ];

        return new DerivedRelationalModelSet(
            GoldenEffectiveSchemaFixtureData.Create(
                "extension-mapping",
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

/// <summary>
/// Fixture for identity propagation fallback scenario (MSSQL only):
/// StudentSchoolAssociation references School via SchoolId FK.
/// On MSSQL, an IdentityPropagationFallback trigger on StudentSchoolAssociation
/// propagates SchoolId changes to the School root table (replacing ON UPDATE CASCADE).
/// On PostgreSQL, only DocumentStamping and ReferentialIdentityMaintenance are emitted.
/// </summary>
internal static class IdentityPropagationFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var schoolDocumentIdColumn = new DbColumnName("School_DocumentId");
        var studentIdColumn = new DbColumnName("StudentUniqueId");
        var entryDateColumn = new DbColumnName("EntryDate");
        var entryTimestampColumn = new DbColumnName("EntryTimestamp");
        var isActiveColumn = new DbColumnName("IsActive");

        // School resource
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var schoolResourceKey = new ResourceKeyEntry(1, schoolResource, "1.0.0", false);

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

        // StudentSchoolAssociation resource
        var assocResource = new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation");
        var assocResourceKey = new ResourceKeyEntry(2, assocResource, "1.0.0", false);

        var assocTableName = new DbTableName(schema, "StudentSchoolAssociation");
        var assocTable = new DbTableModel(
            assocTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StudentSchoolAssociation",
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
                    schoolDocumentIdColumn,
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: schoolResource
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    studentIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    entryDateColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    entryTimestampColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.DateTime),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    isActiveColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Boolean),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_StudentSchoolAssociation_School",
                    [schoolDocumentIdColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
            ]
        );

        var schoolRelationalModel = new RelationalResourceModel(
            schoolResource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable],
            [],
            []
        );

        var assocRelationalModel = new RelationalResourceModel(
            assocResource,
            schema,
            ResourceStorageKind.RelationalTables,
            assocTable,
            [assocTable],
            [],
            []
        );

        // Triggers
        List<DbTriggerInfo> triggers =
        [
            // DocumentStamping on School root
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on School
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "School",
                    [
                        new IdentityElementMapping(
                            schoolIdColumn,
                            "$.schoolId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ]
                )
            ),
            // DocumentStamping on StudentSchoolAssociation root
            new(
                new DbTriggerName("TR_StudentSchoolAssociation_Stamp"),
                assocTableName,
                [documentIdColumn],
                [schoolIdColumn, studentIdColumn, entryDateColumn, entryTimestampColumn, isActiveColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on StudentSchoolAssociation
            new(
                new DbTriggerName("TR_StudentSchoolAssociation_ReferentialIdentity"),
                assocTableName,
                [documentIdColumn],
                [schoolIdColumn, studentIdColumn, entryDateColumn, entryTimestampColumn, isActiveColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    2,
                    "Ed-Fi",
                    "StudentSchoolAssociation",
                    [
                        new IdentityElementMapping(
                            schoolIdColumn,
                            "$.schoolReference.schoolId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                        new IdentityElementMapping(
                            studentIdColumn,
                            "$.studentReference.studentUniqueId",
                            new RelationalScalarType(ScalarKind.String)
                        ),
                        new IdentityElementMapping(
                            entryDateColumn,
                            "$.entryDate",
                            new RelationalScalarType(ScalarKind.Date)
                        ),
                        new IdentityElementMapping(
                            entryTimestampColumn,
                            "$.entryTimestamp",
                            new RelationalScalarType(ScalarKind.DateTime)
                        ),
                        new IdentityElementMapping(
                            isActiveColumn,
                            "$.isActive",
                            new RelationalScalarType(ScalarKind.Boolean)
                        ),
                    ]
                )
            ),
        ];

        // IdentityPropagationFallback — MSSQL only, trigger on referenced entity (School)
        if (dialect == SqlDialect.Mssql)
        {
            triggers.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_School_Propagation"),
                    schoolTableName,
                    [new DbColumnName("DocumentId")],
                    [schoolIdColumn],
                    new TriggerKindParameters.IdentityPropagationFallback([
                        new PropagationReferrerTarget(
                            assocTableName,
                            new DbColumnName("School_DocumentId"),
                            [new TriggerColumnMapping(schoolIdColumn, schoolIdColumn)]
                        ),
                    ])
                )
            );
        }

        return new DerivedRelationalModelSet(
            GoldenEffectiveSchemaFixtureData.Create(
                "identity-propagation",
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [schoolResourceKey, assocResourceKey]
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
                    assocResourceKey,
                    ResourceStorageKind.RelationalTables,
                    assocRelationalModel
                ),
            ],
            [],
            [],
            [],
            triggers
        );
    }
}

/// <summary>
/// Fixture for FK-support index scenario:
/// Enrollment references School via SchoolId FK, with an explicit FK-support index.
/// Tests that FK-support indexes are emitted exactly once and not duplicated.
/// </summary>
internal static class FkSupportIndexFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var enrollmentIdColumn = new DbColumnName("EnrollmentId");

        // School resource (parent)
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var schoolResourceKey = new ResourceKeyEntry(1, schoolResource, "1.0.0", false);

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

        // Enrollment resource (child with FK to School)
        var enrollmentResource = new QualifiedResourceName("Ed-Fi", "Enrollment");
        var enrollmentResourceKey = new ResourceKeyEntry(2, enrollmentResource, "1.0.0", false);

        var enrollmentTableName = new DbTableName(schema, "Enrollment");
        var enrollmentTable = new DbTableModel(
            enrollmentTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_Enrollment", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
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
                    enrollmentIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
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
            [
                new TableConstraint.ForeignKey(
                    "FK_Enrollment_School",
                    [schoolIdColumn],
                    schoolTableName,
                    [schoolIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
            ]
        );

        var schoolRelationalModel = new RelationalResourceModel(
            schoolResource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable],
            [],
            []
        );

        var enrollmentRelationalModel = new RelationalResourceModel(
            enrollmentResource,
            schema,
            ResourceStorageKind.RelationalTables,
            enrollmentTable,
            [enrollmentTable],
            [],
            []
        );

        // FK-support index on Enrollment.SchoolId for join performance
        List<DbIndexInfo> indexes =
        [
            new(
                new DbIndexName("IX_Enrollment_SchoolId"),
                enrollmentTableName,
                [schoolIdColumn],
                IsUnique: false,
                DbIndexKind.ForeignKeySupport
            ),
        ];

        // Minimal triggers (document stamping only)
        List<DbTriggerInfo> triggers =
        [
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            new(
                new DbTriggerName("TR_Enrollment_Stamp"),
                enrollmentTableName,
                [documentIdColumn],
                [enrollmentIdColumn, schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
        ];

        return new DerivedRelationalModelSet(
            GoldenEffectiveSchemaFixtureData.Create(
                "fk-support-index",
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [schoolResourceKey, enrollmentResourceKey]
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
                    enrollmentResourceKey,
                    ResourceStorageKind.RelationalTables,
                    enrollmentRelationalModel
                ),
            ],
            [],
            [],
            indexes,
            triggers
        );
    }
}

/// <summary>
/// Fixture for key unification with triggers scenario:
/// CourseRegistration has two references (courseOfferingReference and schoolReference) that both
/// carry a SchoolId identity field. Key unification merges them into a canonical SchoolId_Unified
/// column with two persisted computed alias columns (CourseOffering_SchoolId, School_SchoolId).
///
/// Validates:
/// - Computed column definitions (PERSISTED for MSSQL, GENERATED ALWAYS AS ... STORED for PostgreSQL)
/// - UPDATE() guards in MSSQL triggers reference the canonical stored column, not aliases
/// - Propagation trigger SET targets reference canonical stored columns, not aliases
/// - Identity hash expressions in ReferentialIdentity triggers read alias columns from inserted/NEW
/// - FK constraints use the DocumentId FK columns (not identity alias columns)
/// </summary>
internal static class KeyUnificationFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");

        // ── School resource (referenced entity, source of propagation) ──
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var schoolResourceKey = new ResourceKeyEntry(1, schoolResource, "1.0.0", false);
        var schoolIdColumn = new DbColumnName("SchoolId");

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

        var schoolRelationalModel = new RelationalResourceModel(
            schoolResource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable],
            [],
            []
        );

        // ── CourseRegistration resource (referrer with key-unified alias columns) ──
        var regResource = new QualifiedResourceName("Ed-Fi", "CourseRegistration");
        var regResourceKey = new ResourceKeyEntry(2, regResource, "1.0.0", false);

        // Column names — post-unification layout
        var courseOfferingDocIdColumn = new DbColumnName("CourseOffering_DocumentId");
        var schoolDocIdColumn = new DbColumnName("School_DocumentId");
        var courseOfferingSchoolIdColumn = new DbColumnName("CourseOffering_SchoolId");
        var schoolSchoolIdColumn = new DbColumnName("School_SchoolId");
        var schoolIdUnifiedColumn = new DbColumnName("SchoolId_Unified");
        var localCourseCodeColumn = new DbColumnName("CourseOffering_LocalCourseCode");
        var registrationDateColumn = new DbColumnName("RegistrationDate");

        var regTableName = new DbTableName(schema, "CourseRegistration");
        var regTable = new DbTableModel(
            regTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_CourseRegistration",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                // DocumentId (PK)
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                // CourseOffering_DocumentId (FK to CourseOffering)
                new DbColumnModel(
                    courseOfferingDocIdColumn,
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: new QualifiedResourceName("Ed-Fi", "CourseOffering")
                ),
                // School_DocumentId (FK to School)
                new DbColumnModel(
                    schoolDocIdColumn,
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: schoolResource
                ),
                // CourseOffering_SchoolId — UNIFIED ALIAS → SchoolId_Unified (presence-gated by CourseOffering_DocumentId)
                new(
                    courseOfferingSchoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.courseOfferingReference.schoolId", []),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(schoolIdUnifiedColumn, courseOfferingDocIdColumn)
                ),
                // CourseOffering_LocalCourseCode — stored identity column
                new DbColumnModel(
                    localCourseCodeColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.courseOfferingReference.localCourseCode", []),
                    TargetResource: null
                ),
                // School_SchoolId — UNIFIED ALIAS → SchoolId_Unified (presence-gated by School_DocumentId)
                new(
                    schoolSchoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.schoolReference.schoolId", []),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(schoolIdUnifiedColumn, schoolDocIdColumn)
                ),
                // RegistrationDate — stored own-identity column
                new DbColumnModel(
                    registrationDateColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.registrationDate", []),
                    TargetResource: null
                ),
                // SchoolId_Unified — canonical stored column (added by key unification)
                new DbColumnModel(
                    schoolIdUnifiedColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_CourseRegistration_CourseOffering",
                    [courseOfferingDocIdColumn],
                    new DbTableName(schema, "CourseOffering"),
                    [documentIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
                new TableConstraint.ForeignKey(
                    "FK_CourseRegistration_School",
                    [schoolDocIdColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
            ]
        )
        {
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    schoolIdUnifiedColumn,
                    [courseOfferingSchoolIdColumn, schoolSchoolIdColumn]
                ),
            ],
        };

        var regRelationalModel = new RelationalResourceModel(
            regResource,
            schema,
            ResourceStorageKind.RelationalTables,
            regTable,
            [regTable],
            [],
            []
        );

        // ── Triggers ──

        // Identity projection columns for CourseRegistration use CANONICAL stored columns only.
        // The two alias columns (CourseOffering_SchoolId, School_SchoolId) resolve to the same
        // canonical SchoolId_Unified, so after de-duplication the projection is:
        //   [SchoolId_Unified, CourseOffering_LocalCourseCode, RegistrationDate]
        IReadOnlyList<DbColumnName> regIdentityProjectionColumns =
        [
            schoolIdUnifiedColumn,
            localCourseCodeColumn,
            registrationDateColumn,
        ];

        List<DbTriggerInfo> triggers =
        [
            // ── School triggers ──
            // DocumentStamping on School root
            new(
                new DbTriggerName("TR_School_Stamp"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on School
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "School",
                    [
                        new IdentityElementMapping(
                            schoolIdColumn,
                            "$.schoolId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ]
                )
            ),
            // ── CourseRegistration triggers ──
            // DocumentStamping on CourseRegistration root (identity projection = canonical columns)
            new(
                new DbTriggerName("TR_CourseRegistration_Stamp"),
                regTableName,
                [documentIdColumn],
                regIdentityProjectionColumns,
                new TriggerKindParameters.DocumentStamping()
            ),
            // ReferentialIdentityMaintenance on CourseRegistration
            // IdentityElements use alias columns (readable from inserted/NEW for hash computation).
            // IdentityProjectionColumns use canonical stored columns (for UPDATE guards).
            new(
                new DbTriggerName("TR_CourseRegistration_ReferentialIdentity"),
                regTableName,
                [documentIdColumn],
                regIdentityProjectionColumns,
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    2,
                    "Ed-Fi",
                    "CourseRegistration",
                    [
                        new IdentityElementMapping(
                            courseOfferingSchoolIdColumn,
                            "$.courseOfferingReference.schoolId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                        new IdentityElementMapping(
                            localCourseCodeColumn,
                            "$.courseOfferingReference.localCourseCode",
                            new RelationalScalarType(ScalarKind.String)
                        ),
                        new IdentityElementMapping(
                            schoolSchoolIdColumn,
                            "$.schoolReference.schoolId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                        new IdentityElementMapping(
                            registrationDateColumn,
                            "$.registrationDate",
                            new RelationalScalarType(ScalarKind.Date)
                        ),
                    ]
                )
            ),
        ];

        // IdentityPropagationFallback — MSSQL only.
        // School propagates SchoolId changes to CourseRegistration.
        // The target column is the CANONICAL stored column (SchoolId_Unified), not the alias.
        if (dialect == SqlDialect.Mssql)
        {
            triggers.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_School_Propagation"),
                    schoolTableName,
                    [documentIdColumn],
                    [schoolIdColumn],
                    new TriggerKindParameters.IdentityPropagationFallback([
                        new PropagationReferrerTarget(
                            regTableName,
                            schoolDocIdColumn,
                            [new TriggerColumnMapping(schoolIdColumn, schoolIdUnifiedColumn)]
                        ),
                    ])
                )
            );
        }

        return new DerivedRelationalModelSet(
            GoldenEffectiveSchemaFixtureData.Create(
                "key-unification",
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [schoolResourceKey, regResourceKey]
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
                    regResourceKey,
                    ResourceStorageKind.RelationalTables,
                    regRelationalModel
                ),
            ],
            [],
            [],
            [],
            triggers
        );
    }
}

/// <summary>
/// Fixture for auth EdOrg hierarchy scenario:
/// Abstract EducationOrganization with StateEducationAgency (leaf) + LocalEducationAgency (hierarchical, parent → SEA).
/// Includes auth hierarchy table, triggers, and covering index.
/// </summary>
internal static class AuthEdOrgHierarchyFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");
        var discriminatorColumn = new DbColumnName("Discriminator");
        var organizationIdColumn = new DbColumnName("EducationOrganizationId");
        var seaParentIdColumn = new DbColumnName("StateEducationAgency_EducationOrganizationId");

        // Abstract resource
        var abstractResource = new QualifiedResourceName("Ed-Fi", "EducationOrganization");
        var abstractResourceKey = new ResourceKeyEntry(1, abstractResource, "1.0.0", true);

        // Concrete resources
        var leaResource = new QualifiedResourceName("Ed-Fi", "LocalEducationAgency");
        var leaResourceKey = new ResourceKeyEntry(2, leaResource, "1.0.0", false);

        var seaResource = new QualifiedResourceName("Ed-Fi", "StateEducationAgency");
        var seaResourceKey = new ResourceKeyEntry(3, seaResource, "1.0.0", false);

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
            [
                new TableConstraint.ForeignKey(
                    "FK_EducationOrganizationIdentity_Document",
                    [documentIdColumn],
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // StateEducationAgency concrete table (leaf)
        var seaTableName = new DbTableName(schema, "StateEducationAgency");
        var seaTable = new DbTableModel(
            seaTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StateEducationAgency",
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
                    "FK_StateEducationAgency_EducationOrganizationIdentity",
                    [documentIdColumn],
                    identityTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        // LocalEducationAgency concrete table (hierarchical, parent FK to SEA)
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
                new DbColumnModel(
                    seaParentIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
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
        var viewName = new DbTableName(schema, "EducationOrganization_View");
        List<AbstractUnionViewOutputColumn> outputColumns =
        [
            new(documentIdColumn, new RelationalScalarType(ScalarKind.Int64), null, null),
            new(organizationIdColumn, new RelationalScalarType(ScalarKind.Int32), null, null),
            new(discriminatorColumn, new RelationalScalarType(ScalarKind.String, MaxLength: 50), null, null),
        ];

        var leaArm = new AbstractUnionViewArm(
            leaResourceKey,
            leaTableName,
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                new AbstractUnionViewProjectionExpression.SourceColumn(organizationIdColumn),
                new AbstractUnionViewProjectionExpression.StringLiteral("Ed-Fi:LocalEducationAgency"),
            ]
        );

        var seaArm = new AbstractUnionViewArm(
            seaResourceKey,
            seaTableName,
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                new AbstractUnionViewProjectionExpression.SourceColumn(organizationIdColumn),
                new AbstractUnionViewProjectionExpression.StringLiteral("Ed-Fi:StateEducationAgency"),
            ]
        );

        var unionView = new AbstractUnionViewInfo(
            abstractResourceKey,
            viewName,
            outputColumns,
            [leaArm, seaArm]
        );

        var abstractIdentityTable = new AbstractIdentityTableInfo(abstractResourceKey, identityTable);

        var leaRelationalModel = new RelationalResourceModel(
            leaResource,
            schema,
            ResourceStorageKind.RelationalTables,
            leaTable,
            [leaTable],
            [],
            []
        );

        var seaRelationalModel = new RelationalResourceModel(
            seaResource,
            schema,
            ResourceStorageKind.RelationalTables,
            seaTable,
            [seaTable],
            [],
            []
        );

        // Auth hierarchy entities (alphabetical order)
        var leaEntity = new AuthEdOrgEntity(
            "LocalEducationAgency",
            leaTableName,
            organizationIdColumn,
            [new AuthParentEdOrgFk(seaParentIdColumn)]
        );

        var seaEntity = new AuthEdOrgEntity("StateEducationAgency", seaTableName, organizationIdColumn, []);

        var authHierarchy = new AuthEdOrgHierarchy([leaEntity, seaEntity]);

        // Standard triggers
        var superclassAlias = new SuperclassAliasInfo(
            1,
            "Ed-Fi",
            "EducationOrganization",
            [
                new IdentityElementMapping(
                    organizationIdColumn,
                    "$.educationOrganizationId",
                    new RelationalScalarType(ScalarKind.Int32)
                ),
            ]
        );

        List<DbTriggerInfo> triggers =
        [
            // DocumentStamping on LEA
            new(
                new DbTriggerName("TR_LocalEducationAgency_Stamp"),
                leaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // AbstractIdentityMaintenance on LEA
            new(
                new DbTriggerName("TR_LocalEducationAgency_AbstractIdentity"),
                leaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.AbstractIdentityMaintenance(
                    identityTableName,
                    [new TriggerColumnMapping(organizationIdColumn, organizationIdColumn)],
                    "Ed-Fi:LocalEducationAgency"
                )
            ),
            // ReferentialIdentityMaintenance on LEA
            new(
                new DbTriggerName("TR_LocalEducationAgency_ReferentialIdentity"),
                leaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    2,
                    "Ed-Fi",
                    "LocalEducationAgency",
                    [
                        new IdentityElementMapping(
                            organizationIdColumn,
                            "$.educationOrganizationId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ],
                    superclassAlias
                )
            ),
            // Auth hierarchy triggers for LEA (hierarchical: INSERT + UPDATE + DELETE)
            new(
                new DbTriggerName("TR_LocalEducationAgency_AuthHierarchy_Delete"),
                leaTableName,
                [],
                [],
                new TriggerKindParameters.AuthHierarchyMaintenance(
                    leaEntity,
                    AuthHierarchyTriggerEvent.Delete
                )
            ),
            new(
                new DbTriggerName("TR_LocalEducationAgency_AuthHierarchy_Insert"),
                leaTableName,
                [],
                [],
                new TriggerKindParameters.AuthHierarchyMaintenance(
                    leaEntity,
                    AuthHierarchyTriggerEvent.Insert
                )
            ),
            new(
                new DbTriggerName("TR_LocalEducationAgency_AuthHierarchy_Update"),
                leaTableName,
                [],
                [],
                new TriggerKindParameters.AuthHierarchyMaintenance(
                    leaEntity,
                    AuthHierarchyTriggerEvent.Update
                )
            ),
            // DocumentStamping on SEA
            new(
                new DbTriggerName("TR_StateEducationAgency_Stamp"),
                seaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.DocumentStamping()
            ),
            // AbstractIdentityMaintenance on SEA
            new(
                new DbTriggerName("TR_StateEducationAgency_AbstractIdentity"),
                seaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.AbstractIdentityMaintenance(
                    identityTableName,
                    [new TriggerColumnMapping(organizationIdColumn, organizationIdColumn)],
                    "Ed-Fi:StateEducationAgency"
                )
            ),
            // ReferentialIdentityMaintenance on SEA
            new(
                new DbTriggerName("TR_StateEducationAgency_ReferentialIdentity"),
                seaTableName,
                [documentIdColumn],
                [organizationIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    3,
                    "Ed-Fi",
                    "StateEducationAgency",
                    [
                        new IdentityElementMapping(
                            organizationIdColumn,
                            "$.educationOrganizationId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ],
                    superclassAlias
                )
            ),
            // Auth hierarchy triggers for SEA (leaf: INSERT + DELETE)
            new(
                new DbTriggerName("TR_StateEducationAgency_AuthHierarchy_Delete"),
                seaTableName,
                [],
                [],
                new TriggerKindParameters.AuthHierarchyMaintenance(
                    seaEntity,
                    AuthHierarchyTriggerEvent.Delete
                )
            ),
            new(
                new DbTriggerName("TR_StateEducationAgency_AuthHierarchy_Insert"),
                seaTableName,
                [],
                [],
                new TriggerKindParameters.AuthHierarchyMaintenance(
                    seaEntity,
                    AuthHierarchyTriggerEvent.Insert
                )
            ),
        ];

        // Auth covering index
        var authIndex = new DbIndexInfo(
            new DbIndexName("IX_EducationOrganizationIdToEducationOrganizationId_Target"),
            AuthNames.EdOrgIdToEdOrgId,
            KeyColumns: [AuthNames.TargetEdOrgId],
            IsUnique: false,
            Kind: DbIndexKind.Authorization,
            IncludeColumns: [AuthNames.SourceEdOrgId]
        );

        return new DerivedRelationalModelSet(
            GoldenEffectiveSchemaFixtureData.Create(
                "auth-edorg-hierarchy",
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [abstractResourceKey, leaResourceKey, seaResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [
                new ConcreteResourceModel(
                    leaResourceKey,
                    ResourceStorageKind.RelationalTables,
                    leaRelationalModel
                ),
                new ConcreteResourceModel(
                    seaResourceKey,
                    ResourceStorageKind.RelationalTables,
                    seaRelationalModel
                ),
            ],
            [abstractIdentityTable],
            [unionView],
            [authIndex],
            triggers,
            authHierarchy
        );
    }
}

// ═══════════════════════════════════════════════════════════════════
// Fixture: People Auth Views
// ═══════════════════════════════════════════════════════════════════

internal static class AuthPeopleViewsFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");
        var organizationIdColumn = new DbColumnName("EducationOrganizationId");

        // Column names used by the people auth views (shared constants with the emitter)
        var schoolIdColumn = AuthNames.SchoolIdUnified;
        var studentDocIdColumn = AuthNames.StudentDocumentId;
        var contactDocIdColumn = AuthNames.ContactDocumentId;
        var staffDocIdColumn = AuthNames.StaffDocumentId;
        var edOrgIdColumn = AuthNames.EdOrgEdOrgId;

        // Resource keys (alphabetical by resource name for deterministic ordering)
        var ssaResource = new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation");
        var ssaResourceKey = new ResourceKeyEntry(1, ssaResource, "1.0.0", false);

        var scaResource = new QualifiedResourceName("Ed-Fi", "StudentContactAssociation");
        var scaResourceKey = new ResourceKeyEntry(2, scaResource, "1.0.0", false);

        var seoaaResource = new QualifiedResourceName(
            "Ed-Fi",
            "StaffEducationOrganizationAssignmentAssociation"
        );
        var seoaaResourceKey = new ResourceKeyEntry(3, seoaaResource, "1.0.0", false);

        var seoeaResource = new QualifiedResourceName(
            "Ed-Fi",
            "StaffEducationOrganizationEmploymentAssociation"
        );
        var seoeaResourceKey = new ResourceKeyEntry(4, seoeaResource, "1.0.0", false);

        var seoraResource = new QualifiedResourceName(
            "Ed-Fi",
            "StudentEducationOrganizationResponsibilityAssociation"
        );
        var seoraResourceKey = new ResourceKeyEntry(5, seoraResource, "1.0.0", false);

        // ── Association tables ──────────────────────────────────────────

        // StudentSchoolAssociation (DocumentId, Student_DocumentId, SchoolId_Unified)
        var ssaTableName = new DbTableName(schema, "StudentSchoolAssociation");
        var ssaTable = new DbTableModel(
            ssaTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StudentSchoolAssociation",
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
                    studentDocIdColumn,
                    ColumnKind.Scalar,
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

        // StudentContactAssociation (DocumentId, Student_DocumentId, Contact_DocumentId)
        var scaTableName = new DbTableName(schema, "StudentContactAssociation");
        var scaTable = new DbTableModel(
            scaTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StudentContactAssociation",
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
                    studentDocIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    contactDocIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // StaffEducationOrganizationAssignmentAssociation (DocumentId, Staff_DocumentId, EducationOrganization_EducationOrganizationId)
        var seoaaTableName = new DbTableName(schema, "StaffEducationOrganizationAssignmentAssociation");
        var seoaaTable = new DbTableModel(
            seoaaTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StaffEducationOrganizationAssignmentAssociation",
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
                    staffDocIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    edOrgIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // StaffEducationOrganizationEmploymentAssociation (DocumentId, Staff_DocumentId, EducationOrganization_EducationOrganizationId)
        var seoeaTableName = new DbTableName(schema, "StaffEducationOrganizationEmploymentAssociation");
        var seoeaTable = new DbTableModel(
            seoeaTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StaffEducationOrganizationEmploymentAssociation",
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
                    staffDocIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    edOrgIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // StudentEducationOrganizationResponsibilityAssociation (DocumentId, Student_DocumentId, EducationOrganization_EducationOrganizationId)
        var seoraTableName = new DbTableName(schema, "StudentEducationOrganizationResponsibilityAssociation");
        var seoraTable = new DbTableModel(
            seoraTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StudentEducationOrganizationResponsibilityAssociation",
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
                    studentDocIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    edOrgIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // ── Relational resource models ──────────────────────────────────

        var ssaModel = new RelationalResourceModel(
            ssaResource,
            schema,
            ResourceStorageKind.RelationalTables,
            ssaTable,
            [ssaTable],
            [],
            []
        );

        var scaModel = new RelationalResourceModel(
            scaResource,
            schema,
            ResourceStorageKind.RelationalTables,
            scaTable,
            [scaTable],
            [],
            []
        );

        var seoaaModel = new RelationalResourceModel(
            seoaaResource,
            schema,
            ResourceStorageKind.RelationalTables,
            seoaaTable,
            [seoaaTable],
            [],
            []
        );

        var seoeaModel = new RelationalResourceModel(
            seoeaResource,
            schema,
            ResourceStorageKind.RelationalTables,
            seoeaTable,
            [seoeaTable],
            [],
            []
        );

        var seoraModel = new RelationalResourceModel(
            seoraResource,
            schema,
            ResourceStorageKind.RelationalTables,
            seoraTable,
            [seoraTable],
            [],
            []
        );

        // ── Auth hierarchy (minimal: single leaf entity to activate view emission) ──

        var seaTableName = new DbTableName(schema, "StateEducationAgency");
        var seaEntity = new AuthEdOrgEntity("StateEducationAgency", seaTableName, organizationIdColumn, []);
        var authHierarchy = new AuthEdOrgHierarchy([seaEntity]);

        // Auth covering index
        var authIndex = new DbIndexInfo(
            new DbIndexName("IX_EducationOrganizationIdToEducationOrganizationId_Target"),
            AuthNames.EdOrgIdToEdOrgId,
            KeyColumns: [AuthNames.TargetEdOrgId],
            IsUnique: false,
            Kind: DbIndexKind.Authorization,
            IncludeColumns: [AuthNames.SourceEdOrgId]
        );

        return new DerivedRelationalModelSet(
            GoldenEffectiveSchemaFixtureData.Create(
                "auth-people-views",
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [ssaResourceKey, scaResourceKey, seoaaResourceKey, seoeaResourceKey, seoraResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [
                new ConcreteResourceModel(ssaResourceKey, ResourceStorageKind.RelationalTables, ssaModel),
                new ConcreteResourceModel(scaResourceKey, ResourceStorageKind.RelationalTables, scaModel),
                new ConcreteResourceModel(seoaaResourceKey, ResourceStorageKind.RelationalTables, seoaaModel),
                new ConcreteResourceModel(seoeaResourceKey, ResourceStorageKind.RelationalTables, seoeaModel),
                new ConcreteResourceModel(seoraResourceKey, ResourceStorageKind.RelationalTables, seoraModel),
            ],
            [],
            [],
            [authIndex],
            [],
            authHierarchy
        );
    }
}

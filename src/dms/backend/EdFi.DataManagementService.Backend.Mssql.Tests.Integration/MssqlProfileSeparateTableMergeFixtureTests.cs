// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// MSSQL twin of PostgresqlProfileSeparateTableMergeFixtureTests. Scenarios, assertions, and
// class names mirror the pgsql sibling one-for-one; only the harness plumbing differs
// (MssqlGeneratedDdlTestDatabase, SqlParameter, bracketed identifiers, TestDatabase-not-
// configured gate). See the pgsql sibling for the narrative on each fixture.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 1: Visible-absent separate-table scope deletes its row (MSSQL).
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_ProfiledUpdate_With_VisibleAbsent_SeparateTableScope_DeletesIt
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("dd000001-0000-0000-0000-000000000001")
    );
    private const int ItemId = 9201;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;
    private int _extRowCountAfterPut;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "OriginalVisible",
                    ["extHiddenScalar"] = "OriginalHidden",
                },
            },
        };
        var seedResult = await MssqlProfileSeparateTableMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "mssql-separate-table-visible-absent-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
        };
        var writePlan = _mappingSet.WritePlansByResource[MssqlProfileSeparateTableMergeSupport.ItemResource];
        var profileContext = MssqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            emitExtRequestScope: true,
            extRequestVisibility: ProfileVisibilityKind.VisibleAbsent,
            extCreatable: true,
            emitExtStoredScope: true,
            extStoredVisibility: ProfileVisibilityKind.VisiblePresent,
            extStoredHiddenMemberPaths: []
        );
        _putResult = await MssqlProfileSeparateTableMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "mssql-separate-table-visible-absent-put"
        );
        _rowAfterPut = await MssqlProfileSeparateTableMergeSupport.ReadRootRowAsync(_database, DocumentUuid);
        _extRowCountAfterPut = await MssqlProfileSeparateTableMergeSupport.CountExtRowsAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_the_root_display_name() =>
        _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_deletes_the_separate_table_row() => _extRowCountAfterPut.Should().Be(0);
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 2: Hidden extension row preserved (MSSQL).
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_ProfiledUpdate_With_Hidden_Extension_Row_PreservesIt
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("dd000002-0000-0000-0000-000000000002")
    );
    private const int ItemId = 9202;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;
    private IReadOnlyDictionary<string, object?>? _extRowAfterPut;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "HiddenRowVisible",
                    ["extHiddenScalar"] = "HiddenRowHidden",
                },
            },
        };
        var seedResult = await MssqlProfileSeparateTableMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "mssql-separate-table-hidden-row-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
        };
        var writePlan = _mappingSet.WritePlansByResource[MssqlProfileSeparateTableMergeSupport.ItemResource];
        var profileContext = MssqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            emitExtRequestScope: false,
            extRequestVisibility: ProfileVisibilityKind.Hidden,
            extCreatable: false,
            emitExtStoredScope: true,
            extStoredVisibility: ProfileVisibilityKind.Hidden,
            extStoredHiddenMemberPaths: []
        );
        _putResult = await MssqlProfileSeparateTableMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "mssql-separate-table-hidden-row-put"
        );
        _rowAfterPut = await MssqlProfileSeparateTableMergeSupport.ReadRootRowAsync(_database, DocumentUuid);
        _extRowAfterPut = await MssqlProfileSeparateTableMergeSupport.TryReadExtRowAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_the_root_display_name() =>
        _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_preserves_the_hidden_extension_row()
    {
        _extRowAfterPut.Should().NotBeNull("hidden extension row must remain after PUT");
        _extRowAfterPut!["ExtVisibleScalar"].Should().Be("HiddenRowVisible");
        _extRowAfterPut["ExtHiddenScalar"].Should().Be("HiddenRowHidden");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 3: Creatable=false for a new visible separate-table scope rejects (MSSQL).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// MSSQL twin of <c>Given_A_ProfiledUpsert_With_Creatable_False_ForNewSeparateTableScope_Rejects</c>.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_ProfiledUpsert_With_Creatable_False_ForNewSeparateTableScope_Rejects
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("dd000003-0000-0000-0000-000000000003")
    );
    private const int ItemId = 9203;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _postResult = null!;
    private int _rootRowCountAfterPost;
    private int _extRowCountAfterPost;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "NewDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "NewVisible",
                    ["extHiddenScalar"] = "NewHidden",
                },
            },
        };
        var writePlan = _mappingSet.WritePlansByResource[MssqlProfileSeparateTableMergeSupport.ItemResource];
        var profileContext = MssqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            emitExtRequestScope: true,
            extRequestVisibility: ProfileVisibilityKind.VisiblePresent,
            extCreatable: false,
            emitExtStoredScope: false,
            extStoredVisibility: ProfileVisibilityKind.VisiblePresent,
            extStoredHiddenMemberPaths: []
        );
        _postResult = await MssqlProfileSeparateTableMergeSupport.ExecuteProfiledPostAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "mssql-separate-table-creatable-false-new-post"
        );

        _rootRowCountAfterPost = await CountRootRowsAsync();
        _extRowCountAfterPost = await MssqlProfileSeparateTableMergeSupport.CountExtRowsAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_profile_data_policy_failure() =>
        _postResult.Should().BeOfType<UpsertResult.UpsertFailureProfileDataPolicy>();

    [Test]
    public void It_does_not_persist_a_root_row() => _rootRowCountAfterPost.Should().Be(0);

    [Test]
    public void It_does_not_persist_a_separate_table_row() => _extRowCountAfterPost.Should().Be(0);

    private async Task<int> CountRootRowsAsync()
    {
        return await _database.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [edfi].[ProfileSeparateTableMergeItem] i
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = i.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new Microsoft.Data.SqlClient.SqlParameter("@documentUuid", DocumentUuid.Value)
        );
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 4: Creatable=false with existing scope allows matched update.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// MSSQL twin of <c>Given_A_ProfiledUpdate_WithExistingSeparateTableScope_And_Creatable_False_AllowsUpdate</c>.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_ProfiledUpdate_WithExistingSeparateTableScope_And_Creatable_False_AllowsUpdate
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("dd000004-0000-0000-0000-000000000004")
    );
    private const int ItemId = 9204;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?>? _extRowAfterPut;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "OriginalVisible",
                    ["extHiddenScalar"] = "OriginalHidden",
                },
            },
        };
        var seedResult = await MssqlProfileSeparateTableMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "mssql-separate-table-creatable-false-matched-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "UpdatedVisible",
                    ["extHiddenScalar"] = "UpdatedHiddenIgnored",
                },
            },
        };
        var writePlan = _mappingSet.WritePlansByResource[MssqlProfileSeparateTableMergeSupport.ItemResource];
        var profileContext = MssqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            emitExtRequestScope: true,
            extRequestVisibility: ProfileVisibilityKind.VisiblePresent,
            extCreatable: false,
            emitExtStoredScope: true,
            extStoredVisibility: ProfileVisibilityKind.VisiblePresent,
            extStoredHiddenMemberPaths: []
        );
        _putResult = await MssqlProfileSeparateTableMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "mssql-separate-table-creatable-false-matched-put"
        );
        _extRowAfterPut = await MssqlProfileSeparateTableMergeSupport.TryReadExtRowAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_the_separate_table_visible_scalar()
    {
        _extRowAfterPut.Should().NotBeNull();
        _extRowAfterPut!["ExtVisibleScalar"].Should().Be("UpdatedVisible");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 5: Hidden descriptor FK on visible separate-table scope is preserved (MSSQL).
//  Closes the spec's "One separate-table hidden-binding preservation case covering
//  FK/descriptor or key-unification/synthetic-presence behavior outside the root row"
//  (03-separate-table-profile-merge.md:119) with a real non-scalar binding.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// MSSQL twin of <c>Given_A_ProfiledUpdate_WithHiddenDescriptorFKOn_SeparateTable_PreservesFK</c>.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_ProfiledUpdate_WithHiddenDescriptorFKOn_SeparateTable_PreservesFK
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("dd000005-0000-0000-0000-000000000005")
    );
    private static readonly Guid DescriptorDocumentUuid = Guid.Parse("dd000005-1000-0000-0000-000000000005");
    private const int ItemId = 9205;
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string DescriptorCodeValue = "MssqlSeparateTableHiddenFK";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _descriptorDocumentId;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?>? _extRowAfterPut;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        _descriptorDocumentId = await MssqlProfileSeparateTableMergeSupport.SeedSchoolTypeDescriptorAsync(
            _database,
            DescriptorDocumentUuid,
            DescriptorNamespace,
            DescriptorCodeValue,
            DescriptorCodeValue
        );

        var seedBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "OriginalVisible",
                    ["extHiddenScalar"] = "OriginalHidden",
                },
            },
        };
        var seedResult = await MssqlProfileSeparateTableMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "mssql-separate-table-hidden-descriptor-fk-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        // Populate the descriptor FK column on the extension row directly.
        await MssqlProfileSeparateTableMergeSupport.SetExtRowSampleCategoryDescriptorAsync(
            _database,
            DocumentUuid,
            _descriptorDocumentId
        );

        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject { ["extVisibleScalar"] = "UpdatedVisible" },
            },
        };
        var writePlan = _mappingSet.WritePlansByResource[MssqlProfileSeparateTableMergeSupport.ItemResource];
        var profileContext = MssqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            emitExtRequestScope: true,
            extRequestVisibility: ProfileVisibilityKind.VisiblePresent,
            extCreatable: true,
            emitExtStoredScope: true,
            extStoredVisibility: ProfileVisibilityKind.VisiblePresent,
            extStoredHiddenMemberPaths: ["sampleCategoryDescriptor"]
        );
        _putResult = await MssqlProfileSeparateTableMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "mssql-separate-table-hidden-descriptor-fk-put"
        );
        _extRowAfterPut = await MssqlProfileSeparateTableMergeSupport.TryReadExtRowAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_the_visible_scalar()
    {
        _extRowAfterPut.Should().NotBeNull();
        _extRowAfterPut!["ExtVisibleScalar"].Should().Be("UpdatedVisible");
    }

    [Test]
    public void It_preserves_the_hidden_descriptor_fk()
    {
        _extRowAfterPut.Should().NotBeNull();
        _extRowAfterPut!["SampleCategoryDescriptor_DescriptorId"].Should().Be(_descriptorDocumentId);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture 6: Hidden whole-scope preservation (MSSQL).
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_ProfiledUpdate_WithHiddenWholeSeparateTableScope_PreservesRow
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("dd000006-0000-0000-0000-000000000006")
    );
    private const int ItemId = 9206;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;
    private IReadOnlyDictionary<string, object?>? _extRowAfterPut;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileSeparateTableMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileSeparateTableMergeSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["extVisibleScalar"] = "WholeScopeVisible",
                    ["extHiddenScalar"] = "WholeScopeHidden",
                },
            },
        };
        var seedResult = await MssqlProfileSeparateTableMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "mssql-separate-table-hidden-whole-scope-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["profileSeparateTableMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
        };
        var writePlan = _mappingSet.WritePlansByResource[MssqlProfileSeparateTableMergeSupport.ItemResource];
        var profileContext = MssqlProfileSeparateTableMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            emitExtRequestScope: true,
            extRequestVisibility: ProfileVisibilityKind.VisiblePresent,
            extCreatable: true,
            emitExtStoredScope: true,
            extStoredVisibility: ProfileVisibilityKind.Hidden,
            extStoredHiddenMemberPaths: []
        );
        _putResult = await MssqlProfileSeparateTableMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            writeBody,
            DocumentUuid,
            profileContext,
            "mssql-separate-table-hidden-whole-scope-put"
        );
        _rowAfterPut = await MssqlProfileSeparateTableMergeSupport.ReadRootRowAsync(_database, DocumentUuid);
        _extRowAfterPut = await MssqlProfileSeparateTableMergeSupport.TryReadExtRowAsync(
            _database,
            DocumentUuid
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_the_root_display_name() =>
        _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_preserves_both_separate_table_scalars_untouched()
    {
        _extRowAfterPut.Should().NotBeNull("hidden-whole-scope must preserve the row");
        _extRowAfterPut!["ExtVisibleScalar"].Should().Be("WholeScopeVisible");
        _extRowAfterPut["ExtHiddenScalar"].Should().Be("WholeScopeHidden");
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_CdcProjectionPrerequisite_Managed_Provisioning
{
    private IDatabaseProvisioner _provider = null!;
    private IDocumentCachePhysicalSourceFingerprintReader _reader = null!;
    private readonly List<string> _trace = [];
    private static readonly EffectiveSchemaInfo Schema = new("1", "1", "hash", 0, [], [], []);
    private static readonly CdcTargetIdentity Target = new(
        "local",
        "default",
        "42",
        "datastore-42",
        1,
        CdcProvider.SqlServer
    );
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _trace.Clear();
        _root = Path.Combine(Path.GetTempPath(), "cdc-prerequisite-" + Guid.NewGuid().ToString("N"));
        _provider = A.Fake<IDatabaseProvisioner>();
        _reader = A.Fake<IDocumentCachePhysicalSourceFingerprintReader>();
        A.CallTo(() => _provider.CreateDatabaseIfNotExists("connection"))
            .Invokes(() => _trace.Add("create"))
            .Returns(true);
        A.CallTo(() => _provider.CheckOrConfigureMvcc("connection", A<bool>._))
            .Invokes(() => _trace.Add("mvcc"));
        A.CallTo(() => _provider.CheckCdcProjectionPrerequisites("connection", A<bool>._))
            .Invokes(() => _trace.Add("prerequisites"));
        A.CallTo(() => _provider.PreflightSeedValidation("connection", Schema))
            .Invokes(() => _trace.Add("schema-preflight"));
        A.CallTo(() => _provider.ExecuteInTransaction("connection", "ddl", 30))
            .Invokes(() => _trace.Add("ddl"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private ManagedDatabaseProvisioner Adapter(CdcProjectionPrerequisiteMode mode) =>
        new(_provider, _reader, "connection", Schema, "ddl", 30, mode);

    [TestCase(true, CdcProjectionPrerequisiteMode.OwnedLocalSqlServer, true)]
    [TestCase(false, CdcProjectionPrerequisiteMode.OwnedLocalSqlServer, false)]
    [TestCase(true, CdcProjectionPrerequisiteMode.Inspect, false)]
    [TestCase(false, CdcProjectionPrerequisiteMode.Inspect, false)]
    public void It_prepares_before_schema_and_requires_separate_server_authority(
        bool created,
        CdcProjectionPrerequisiteMode mode,
        bool configure
    )
    {
        Adapter(mode).ProvisionSchema(created);
        _trace.Should().Equal("mvcc", "prerequisites", "schema-preflight", "ddl");
        A.CallTo(() => _provider.CheckOrConfigureMvcc("connection", created)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _provider.CheckCdcProjectionPrerequisites("connection", configure))
            .MustHaveHappenedOnceExactly();
    }

    [TestCase(CdcProjectionPrerequisiteMode.OwnedLocalSqlServer)]
    [TestCase(CdcProjectionPrerequisiteMode.Inspect)]
    public void It_inspects_current_prerequisites_on_retry_without_repair(CdcProjectionPrerequisiteMode mode)
    {
        Adapter(mode).ValidateSchema();
        _trace.Should().Equal("prerequisites", "schema-preflight");
        A.CallTo(() => _provider.CheckCdcProjectionPrerequisites("connection", false))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _provider.CheckOrConfigureMvcc(A<string>._, A<bool>._)).MustNotHaveHappened();
    }

    [Test]
    public void It_preserves_ordinary_provisioning()
    {
        Adapter(CdcProjectionPrerequisiteMode.None).ProvisionSchema(true);
        Adapter(CdcProjectionPrerequisiteMode.None).ValidateSchema();
        _trace.Should().Equal("mvcc", "schema-preflight", "ddl", "schema-preflight");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_preserves_the_create_receipt_and_stops_before_schema_or_source_when_preparation_fails(
        bool mvccFailure
    )
    {
        if (mvccFailure)
        {
            A.CallTo(() => _provider.CheckOrConfigureMvcc("connection", true))
                .Throws<InvalidOperationException>();
        }
        else
        {
            A.CallTo(() => _provider.CheckCdcProjectionPrerequisites("connection", true))
                .Throws<InvalidOperationException>();
        }
        var store = new LocalCdcWorkflowJournalStore(_root);
        var controller = new CdcManagedDatabaseProvisioning(store);
        var adapter = Adapter(CdcProjectionPrerequisiteMode.OwnedLocalSqlServer);
        await FluentActions
            .Awaiting(() => controller.ProvisionAsync(Target, adapter))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        A.CallTo(() => _provider.ExecuteInTransaction(A<string>._, A<string>._, A<int>._))
            .MustNotHaveHappened();
        A.CallTo(() => _reader.ReadFingerprintAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        await using (
            var session = await store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(10),
                default
            )
        )
        {
            var journal = await session.ReadAsync(Target, default);
            journal.Operations.Should().ContainSingle();
            ((CdcWorkflowCompletion.Database)journal.Operations[0].Completions.Single().Evidence)
                .Receipt.Outcome.Should()
                .Be(CdcDatabaseCreationOutcome.Created);
        }
        await FluentActions
            .Awaiting(() => controller.ProvisionAsync(Target, adapter))
            .Should()
            .ThrowAsync<CdcManagedProvisioningRecoveryException>();
        A.CallTo(() => _provider.CreateDatabaseIfNotExists("connection")).MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_rejects_unavailable_or_disabled_prerequisites_on_retry()
    {
        A.CallTo(() => _provider.CheckCdcProjectionPrerequisites("connection", false))
            .Throws<InvalidOperationException>();
        FluentActions
            .Invoking(() => Adapter(CdcProjectionPrerequisiteMode.OwnedLocalSqlServer).ValidateSchema())
            .Should()
            .Throw<InvalidOperationException>();
        A.CallTo(() => _provider.PreflightSeedValidation(A<string>._, A<EffectiveSchemaInfo>._))
            .MustNotHaveHappened();
    }
}

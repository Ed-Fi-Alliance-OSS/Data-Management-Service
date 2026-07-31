// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Commands;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_SchemaTools_DdlCommands_For_CdcProviderSetupService
{
    [Test]
    public void It_should_not_expose_cdc_provider_setup_options_on_ordinary_ddl_commands()
    {
        var commands = new[]
        {
            DdlEmitCommand.Create(
                A.Fake<ILogger>(),
                A.Fake<IApiSchemaFileLoader>(),
                CreateSchemaSetBuilder()
            ),
            DdlProvisionCommand.Create(
                A.Fake<ILogger>(),
                A.Fake<IApiSchemaFileLoader>(),
                CreateSchemaSetBuilder()
            ),
        };

        var optionNames = commands.SelectMany(command => command.Options).Select(option => option.Name);

        optionNames.Should().NotContain("cdc-provider");
        optionNames.Should().NotContain("connector-principal");
        optionNames.Should().NotContain("publication-name");
        optionNames.Should().NotContain("replication-slot-name");
        optionNames.Should().NotContain("capture-instance-name");
        optionNames.Should().NotContain("heartbeat-action-query");
    }

    [TestFixture(SqlDialect.Pgsql)]
    [TestFixture(SqlDialect.Mssql)]
    public class Given_Ordinary_Ddl_Emission(SqlDialect dialect)
    {
        private string _ordinaryDdl = null!;

        [SetUp]
        public void SetUp()
        {
            _ordinaryDdl = new CoreDdlEmitter(SqlDialectFactory.Create(dialect)).Emit();
        }

        [Test]
        public void It_should_not_call_cdc_provider_setup_or_emit_provider_artifacts()
        {
            _ordinaryDdl.Should().NotContain("CdcHeartbeat");
            _ordinaryDdl.Should().NotContain("CREATE PUBLICATION");
            _ordinaryDdl.Should().NotContain("pg_create_logical_replication_slot");
            _ordinaryDdl.Should().NotContain("sp_cdc_enable_table");
            _ordinaryDdl.Should().NotContain("sp_cdc_enable_db");
        }
    }

    private static EffectiveSchemaSetBuilder CreateSchemaSetBuilder() =>
        new(A.Fake<IEffectiveSchemaHashProvider>(), A.Fake<IResourceKeySeedProvider>());
}

[TestFixture]
public class Given_SchemaTools_CdcProviderRetryContract
{
    [Test]
    public void It_should_not_expose_retry_repair_or_offset_reset_options_on_ordinary_ddl_commands()
    {
        var commands = new[]
        {
            DdlEmitCommand.Create(
                A.Fake<ILogger>(),
                A.Fake<IApiSchemaFileLoader>(),
                CreateSchemaSetBuilder()
            ),
            DdlProvisionCommand.Create(
                A.Fake<ILogger>(),
                A.Fake<IApiSchemaFileLoader>(),
                CreateSchemaSetBuilder()
            ),
        };

        var optionNames = commands.SelectMany(command => command.Options).Select(option => option.Name);

        optionNames.Should().NotContain("cdc-setup-mode");
        optionNames.Should().NotContain("cdc-validate-only");
        optionNames.Should().NotContain("cdc-repair");
        optionNames.Should().NotContain("cdc-reset-offset");
        optionNames.Should().NotContain("drop-replication-slot");
        optionNames.Should().NotContain("drop-capture-instance");
    }

    private static EffectiveSchemaSetBuilder CreateSchemaSetBuilder() =>
        new(A.Fake<IEffectiveSchemaHashProvider>(), A.Fake<IResourceKeySeedProvider>());
}

[TestFixture]
public class Given_SchemaTools_CdcArtifactNames
{
    [Test]
    public void It_should_not_expose_cdc_artifact_name_generation_on_ordinary_ddl_commands()
    {
        var commands = new[]
        {
            DdlEmitCommand.Create(
                A.Fake<ILogger>(),
                A.Fake<IApiSchemaFileLoader>(),
                CreateSchemaSetBuilder()
            ),
            DdlProvisionCommand.Create(
                A.Fake<ILogger>(),
                A.Fake<IApiSchemaFileLoader>(),
                CreateSchemaSetBuilder()
            ),
        };

        var optionNames = commands.SelectMany(command => command.Options).Select(option => option.Name);

        optionNames.Should().NotContain("cdc-artifact-name");
        optionNames.Should().NotContain("cdc-binding-generation");
        optionNames.Should().NotContain("cdc-deployment-key");
        optionNames.Should().NotContain("cdc-instance-key");
        optionNames.Should().NotContain("tenant-display-name");
        optionNames.Should().NotContain("connection-string");
        optionNames.Should().NotContain("database-name");
        optionNames.Should().NotContain("publication-name");
        optionNames.Should().NotContain("replication-slot-name");
        optionNames.Should().NotContain("capture-instance-name");
        optionNames.Should().NotContain("gating-role-name");
    }

    private static EffectiveSchemaSetBuilder CreateSchemaSetBuilder() =>
        new(A.Fake<IEffectiveSchemaHashProvider>(), A.Fake<IResourceKeySeedProvider>());
}

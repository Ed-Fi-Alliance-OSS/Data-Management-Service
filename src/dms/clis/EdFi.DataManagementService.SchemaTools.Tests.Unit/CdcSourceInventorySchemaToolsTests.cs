// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_SchemaTools_OrdinaryDdl_For_CdcSourceInventory(SqlDialect dialect)
{
    private string _ordinaryDdl = null!;

    [SetUp]
    public void SetUp()
    {
        _ordinaryDdl = new CoreDdlEmitter(SqlDialectFactory.Create(dialect)).Emit();
    }

    [Test]
    public void It_should_keep_the_opt_in_heartbeat_out_of_ordinary_ddl()
    {
        _ordinaryDdl.Should().NotContain("CdcHeartbeat");
    }

    [Test]
    public void It_should_keep_provider_artifacts_out_of_ordinary_ddl()
    {
        _ordinaryDdl.Should().NotContain("CREATE PUBLICATION");
        _ordinaryDdl.Should().NotContain("pg_create_logical_replication_slot");
        _ordinaryDdl.Should().NotContain("sp_cdc_enable_table");
        _ordinaryDdl.Should().NotContain("cdc_enable_db");
    }

    [Test]
    public void It_should_still_emit_the_always_provisioned_projection_tables()
    {
        _ordinaryDdl.Should().Contain("DocumentCache");
        _ordinaryDdl.Should().Contain("DocumentProjectionWork");
        _ordinaryDdl.Should().Contain("DocumentCacheState");
    }
}

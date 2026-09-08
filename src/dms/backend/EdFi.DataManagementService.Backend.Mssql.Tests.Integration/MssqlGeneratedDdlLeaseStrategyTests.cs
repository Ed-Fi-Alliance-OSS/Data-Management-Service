// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlLeaseStrategy
{
    [Test]
    public void It_defaults_to_snapshot_slot_when_the_value_is_empty()
    {
        MssqlGeneratedDdlLeaseStrategy.Parse(null).Should().Be(MssqlGeneratedDdlLeaseStrategy.SnapshotSlot);
        MssqlGeneratedDdlLeaseStrategy.Parse("").Should().Be(MssqlGeneratedDdlLeaseStrategy.SnapshotSlot);
        MssqlGeneratedDdlLeaseStrategy.Parse("   ").Should().Be(MssqlGeneratedDdlLeaseStrategy.SnapshotSlot);
    }

    [TestCase("snapshot-slot", MssqlGeneratedDdlLeaseStrategy.SnapshotSlot)]
    [TestCase(" SNAPSHOT-SLOT ", MssqlGeneratedDdlLeaseStrategy.SnapshotSlot)]
    [TestCase("backup-restore", MssqlGeneratedDdlLeaseStrategy.BackupRestore)]
    [TestCase(" BACKUP-RESTORE ", MssqlGeneratedDdlLeaseStrategy.BackupRestore)]
    public void It_accepts_supported_values(string value, string expectedStrategy)
    {
        MssqlGeneratedDdlLeaseStrategy.Parse(value).Should().Be(expectedStrategy);
    }

    [Test]
    public void It_rejects_unsupported_values_with_a_clear_error()
    {
        Action act = () => MssqlGeneratedDdlLeaseStrategy.Parse("copy-database");

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                $"*{MssqlGeneratedDdlLeaseStrategy.EnvironmentVariableName}*copy-database*{MssqlGeneratedDdlLeaseStrategy.SnapshotSlot}*{MssqlGeneratedDdlLeaseStrategy.BackupRestore}*"
            );
    }
}

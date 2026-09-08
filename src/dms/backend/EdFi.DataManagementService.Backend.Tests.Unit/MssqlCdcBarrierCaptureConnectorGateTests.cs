// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Backend.Mssql;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The SQL Server provider barrier capture waits for a heartbeat after-image past the sequence it reads,
/// and those rows are written by the connector's own <c>heartbeat.action.query</c>. A caller that has
/// observed the connector unable to produce one says so, and the capture declines rather than spending
/// its whole wait on evidence that cannot arrive.
/// </summary>
/// <remarks>
/// A unit fixture rather than an integration one because the property under test is that the capture
/// never reaches the database at all: the connection string below points at a port nothing listens on,
/// so a pass that opened a connection would fail on the connection rather than return this result. That
/// also makes the assertion independent of a running SQL Server, unlike the rest of the adapter's cases.
/// </remarks>
[TestFixture]
[Parallelizable]
[Category("CdcProviderPosition")]
public class Given_MssqlCdcBarrierCaptureAgainstAConnectorThatCannotAdvance
{
    private const string UnreachableConnectionString =
        "Server=127.0.0.1,1;Database=edfi_datastore;User Id=sa;Password=unused;"
        + "Encrypt=False;Connect Timeout=1";

    [Test]
    public async Task It_declines_the_capture_without_reaching_the_database()
    {
        MssqlCdcSourcePositionAdapter adapter = Adapter();

        Stopwatch elapsed = Stopwatch.StartNew();
        CoreCdc.CdcProviderBarrierCaptureResult captured = await adapter.CaptureBarrierAsync(
            new(UnreachableConnectionString, Binding())
            {
                // The wait a running connector would be given. Nothing may consume it here.
                CaptureWaitTimeout = TimeSpan.FromMinutes(10),
                PollInterval = TimeSpan.FromSeconds(2),
                ConnectorCanAdvance = false,
            },
            CancellationToken.None
        );
        elapsed.Stop();

        captured.Succeeded.Should().BeFalse();
        captured.SqlServerCommitLsn.Should().BeNull();
        captured.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("was not attempted");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The complement, which is what keeps the gate from being a way to skip the capture altogether: a
    /// caller that reports the connector able to advance gets the real pass, which here means the
    /// connection attempt this fixture's unreachable address refuses.
    /// </summary>
    [Test]
    public async Task It_attempts_the_capture_when_the_connector_can_advance()
    {
        MssqlCdcSourcePositionAdapter adapter = Adapter();

        CoreCdc.CdcProviderBarrierCaptureResult captured = await adapter.CaptureBarrierAsync(
            new(UnreachableConnectionString, Binding())
            {
                CaptureWaitTimeout = TimeSpan.FromSeconds(1),
                PollInterval = TimeSpan.FromMilliseconds(100),
                ConnectorCanAdvance = true,
            },
            CancellationToken.None
        );

        captured.Succeeded.Should().BeFalse();
        captured.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("capture failed");
    }

    private static MssqlCdcSourcePositionAdapter Adapter() =>
        new(
            new MssqlDocumentCacheProviderCommandTimeoutClassifier(),
            TimeProvider.System,
            NullLogger<MssqlCdcSourcePositionAdapter>.Instance
        );

    private static CoreCdc.CdcBinding Binding()
    {
        const string InstanceKey = "edfi_datastore";
        CoreCdc.CdcArtifactInventory inventory = CoreCdc
            .CdcArtifactNameGenerator.Render(
                new("dms-local", "edfi.dms", InstanceKey, 1, CoreCdc.CdcProvider.SqlServer)
            )
            .Inventory!;
        string fingerprint = CoreCdc
            .CdcPhysicalSourceFingerprintCalculator.Compute(
                CoreCdc.CdcProvider.SqlServer,
                Guid.Parse("f81d4fae-7dec-11d0-a765-00a0c91e6bf6")
            )
            .Fingerprint!;

        return new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            "dms-local",
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            InstanceKey,
            1,
            CoreCdc.CdcProvider.SqlServer,
            fingerprint,
            inventory.ConnectorName,
            inventory.TopicName,
            3,
            CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CoreCdc.CdcJsonContract.CurrentContractVersion
        );
    }
}

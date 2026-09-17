// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(42L)]
[TestFixture(long.MinValue)]
public class Given_CdcPostgresqlInitialOffset(long lsn)
{
    private CdcConnectOffsetEvidence _evidence = null!;
    private string _expectedHash = "";

    [SetUp]
    public async Task Setup()
    {
        using CdcConnectHttpFixture fixture = new(CdcProvider.Postgresql);
        // The pinned Debezium PostgresOffsetContext.initialContext/getOffset emits
        // these three fields before a completely processed LSN is available.
        fixture.Respond(body: fixture.Offsets($$"""{"lsn":{{lsn}},"txId":1,"ts_usec":1000000}"""));
        var result = await fixture.Adapter.ReadOffsetEvidenceAsync(fixture.Request, CancellationToken.None);
        _evidence = result
            .Should()
            .BeOfType<CdcTransportResult<CdcConnectOffsetEvidence>.Observed>()
            .Which.Value;
        _expectedHash = CoreCdc
            .CdcSourcePartitionHashCalculator.Compute(
                fixture.Request.Binding.Provider,
                fixture.Request.Binding.ConnectorName,
                null
            )
            .Hash!;
    }

    [Test]
    public void It_waits_for_streaming() =>
        _evidence.State.Should().Be(CdcConnectOffsetState.AwaitingStreaming);

    [Test]
    public void It_preserves_source_identity() => _evidence.SourcePartitionHash.Should().Be(_expectedHash);

    [Test]
    public void It_supplies_no_committed_provider_position() =>
        _evidence.Postgresql.LsnProc.Should().BeNull();

    [Test]
    public void It_cannot_satisfy_a_provider_barrier() =>
        CoreCdc
            .CdcPostgresqlProviderPosition.CompareCommittedOffsetToBarrier(new(0), _evidence.Postgresql)
            .Succeeded.Should()
            .BeFalse();

    [Test]
    public void It_does_not_serialize_offset_metadata() =>
        JsonSerializer.Serialize(_evidence).Should().NotContain("1000000").And.NotContain("txId");
}

[TestFixture]
public class Given_CdcPostgresqlUnrecognizedInitialOffset
{
    [TestCase("{}")]
    [TestCase("""{"lsn":42,"txId":1}""")]
    [TestCase("""{"lsn":42,"ts_usec":1000000}""")]
    [TestCase("""{"txId":1,"ts_usec":1000000}""")]
    [TestCase("""{"lsn":0,"txId":1,"ts_usec":1000000}""")]
    [TestCase("""{"lsn":"42","txId":1,"ts_usec":1000000}""")]
    [TestCase("""{"lsn":null,"txId":1,"ts_usec":1000000}""")]
    [TestCase("""{"lsn":1.5,"txId":1,"ts_usec":1000000}""")]
    [TestCase("""{"lsn":18446744073709551615,"txId":1,"ts_usec":1000000}""")]
    [TestCase("""{"lsn":42,"txId":0,"ts_usec":1000000}""")]
    [TestCase("""{"lsn":42,"txId":-1,"ts_usec":1000000}""")]
    [TestCase("""{"lsn":42,"txId":"1","ts_usec":1000000}""")]
    [TestCase("""{"lsn":42,"txId":null,"ts_usec":1000000}""")]
    [TestCase("""{"lsn":42,"txId":1.5,"ts_usec":1000000}""")]
    [TestCase("""{"lsn":42,"txId":1,"ts_usec":0}""")]
    [TestCase("""{"lsn":42,"txId":1,"ts_usec":-1}""")]
    [TestCase("""{"lsn":42,"txId":1,"ts_usec":"1000000"}""")]
    [TestCase("""{"lsn":42,"txId":1,"ts_usec":null}""")]
    [TestCase("""{"lsn":42,"txId":1,"ts_usec":1.5}""")]
    [TestCase("""{"lsn":42,"txId":1,"ts_usec":1000000,"lsn_proc":null}""")]
    [TestCase("""{"lsn":42,"txId":1,"ts_usec":1000000,"lsn_proc":"42"}""")]
    [TestCase("""{"lsn":42,"txId":1,"ts_usec":1000000,"lsn_commit":42}""")]
    [TestCase("""{"lsn":42,"txId":1,"ts_usec":1000000,"unknown":42}""")]
    [TestCase("""{"lsn":42,"txId":1,"ts_usec":1000000,"snapshot":"unexpected"}""")]
    public async Task It_rejects_unrecognized_or_malformed_shapes(string offset)
    {
        using CdcConnectHttpFixture fixture = new(CdcProvider.Postgresql);
        fixture.Respond(body: fixture.Offsets(offset));
        var result = await fixture.Adapter.ReadOffsetEvidenceAsync(fixture.Request, CancellationToken.None);
        var evidence = result
            .Should()
            .BeOfType<CdcTransportResult<CdcConnectOffsetEvidence>.Observed>()
            .Which.Value;
        evidence.State.Should().Be(CdcConnectOffsetState.Malformed);
        evidence.Postgresql.LsnProc.Should().BeNull();
    }
}

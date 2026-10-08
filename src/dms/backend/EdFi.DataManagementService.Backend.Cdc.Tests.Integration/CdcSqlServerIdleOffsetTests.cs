// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcProviderAdmissionFixture;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category("MssqlIntegration")]
[Category(CdcControllerCategories.ManagedLifecycle)]
[Category("DatabaseIntegration")]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_SqlServer_Idle_Streaming_Offset
{
    private CdcProviderAdmissionFixture _fixture = null!;
    private CancellationTokenSource _timeout = null!;

    [SetUp]
    public async Task Setup()
    {
        _timeout = new(TimeSpan.FromMinutes(10));
        _fixture = await CdcProviderAdmissionFixture.StartAsync(CdcProvider.SqlServer, _timeout.Token);
        await _fixture.RegisterAsync(_timeout.Token);
        Observed(
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _fixture.Request,
                _fixture.Runtime,
                60_000,
                _timeout.Token
            )
        );
        await _fixture.ReopenRuntimeAsync(_timeout.Token);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
        _timeout.Dispose();
    }

    [Test]
    public async Task It_accepts_a_real_idle_commit_boundary_without_bypassing_capture_job_health()
    {
        // The normal heartbeat query writes a captured row every second. Temporarily pause
        // this fixture database's capture job so restart can reread only committed rows and
        // publish its idle boundary. Restore capture afterward, preserving the cancelled
        // job history: accepting an idle offset must not bypass the independent job guard.
        await _fixture.ExecuteAsync("EXEC sys.sp_cdc_stop_job @job_type=N'capture';", _timeout.Token);
        try
        {
            Observed(await _fixture.Infrastructure.Connect.StopAsync(_fixture.Request, _timeout.Token));
            await CdcControllerFixture.WaitAsync(
                async ct =>
                    Observed(
                        await _fixture.Infrastructure.Connect.ReadStatusAsync(_fixture.Request, ct)
                    ).IsStopped,
                _fixture.Request.Timing.WaitTimeout,
                TimeSpan.FromSeconds(1),
                _timeout.Token
            );
            Observed(await _fixture.Infrastructure.Connect.ResumeAsync(_fixture.Request, _timeout.Token));
            await CdcControllerFixture.WaitAsync(
                async ct =>
                {
                    var offset = Observed(
                        await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(_fixture.Request, ct)
                    );
                    using var document = JsonDocument.Parse(
                        JsonSerializer.Serialize(_fixture.Infrastructure.OffsetEvidence.Observations[^1])
                    );
                    var response = document.RootElement;
                    if (
                        !response.TryGetProperty("Entries", out var entries)
                        || entries.GetArrayLength() != 1
                        || !entries[0].GetProperty("Change").GetProperty("NullMarker").GetBoolean()
                        || !entries[0].GetProperty("SerialZero").GetBoolean()
                    )
                    {
                        return false;
                    }
                    offset.State.Should().Be(CdcConnectOffsetState.Streaming);
                    offset.SqlServer.ChangeLsn.Should().Be("NULL");
                    offset.SqlServer.EventSerialNo.Should().Be(0);
                    return true;
                },
                _fixture.Request.Timing.WaitTimeout,
                TimeSpan.FromSeconds(1),
                _timeout.Token
            );
        }
        finally
        {
            await _fixture.ExecuteAsync("EXEC sys.sp_cdc_start_job @job_type=N'capture';", _timeout.Token);
        }
        await CdcControllerFixture.WaitAsync(
            async ct =>
            {
                var validation = Observed(
                    await _fixture.Controllers.Validation.ValidateAsync(
                        _fixture.Request,
                        _fixture.Runtime,
                        CdcEstablishedValidationMode.PreStart,
                        60_000,
                        cancellationToken: ct
                    )
                );
                validation.PublicationReady.Should().BeFalse();
                if (
                    validation.SourceHistory.Observation.SqlServerJobs?.CaptureJobState
                    != CoreCdc.CdcSqlServerCdcJobState.Failed
                )
                {
                    return false;
                }
                validation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Unknown);
                validation.PreStartEligible.Should().BeFalse();
                validation.SourceHistory.IncidentCandidate.Should().BeNull();
                validation
                    .SourceHistory.Observation.Diagnostics.Should()
                    .Contain(d => d.Path == "$.providerHistory.sqlServerJobs");
                return true;
            },
            _fixture.Request.Timing.WaitTimeout,
            TimeSpan.FromSeconds(1),
            _timeout.Token
        );
    }
}

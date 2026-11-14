// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.CompilerServices;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.ProvisioningTestValues;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// The SQL Server side of the dispatch boundary without a server: the fence, the owned drop, the rename and the
/// marker check the token before their batch is handed to the driver, and a cancellation found there is reported as
/// not dispatched; so does the staging reservation, which never sends anything. The connection is never opened, so
/// nothing could have been sent.
/// </summary>
public class MssqlDispatchBoundaryTests
{
    private static readonly DmsInstanceIdentity.Mssql _instance = new(
        7,
        "edfi_ods_test.mdf",
        new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)
    );

    [TestFixture]
    public class Given_the_token_is_cancelled_before_a_mutation_is_sent
    {
        private readonly List<Exception?> _outcomes = [];

        [SetUp]
        public async Task Setup()
        {
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();
            await using SqlConnection connection = new();
            DmsTargetDatabaseName target = Name("EdFi_Ods_Test");

            _outcomes.Add(
                await Record.ExceptionAsync(() =>
                    MssqlDeletionQueries.FenceAsync(
                        connection,
                        target,
                        _instance,
                        Session,
                        TimeSpan.FromSeconds(30),
                        30,
                        TimeProvider.System,
                        null,
                        cancelled.Token
                    )
                )
            );
            _outcomes.Add(
                await Record.ExceptionAsync(() =>
                    MssqlDeletionQueries.DropTargetAsync(
                        connection,
                        target,
                        _instance,
                        Session,
                        30,
                        cancelled.Token
                    )
                )
            );
            _outcomes.Add(
                await Record.ExceptionAsync(() =>
                    MssqlPublicationQueries.RenameAsync(
                        connection,
                        DmsOperationId.New().StagingDatabaseName,
                        _instance,
                        target,
                        Session,
                        30,
                        cancelled.Token
                    )
                )
            );
            _outcomes.Add(
                await Record.ExceptionAsync(() =>
                    MssqlPublicationQueries.AssignMarkerAsync(
                        connection,
                        _instance,
                        Guid.NewGuid(),
                        Session,
                        30,
                        cancelled.Token
                    )
                )
            );

            // The real engine's reservation, which only checks the holder's type: a holder that never connected.
            MssqlDmsTargetEngine engine = new(
                new MssqlAdminConnection(
                    "Server=127.0.0.1,1;User Id=sa;Password=unused;Connect Timeout=1;TrustServerCertificate=true",
                    TimeSpan.FromSeconds(5)
                ),
                MssqlEngineSettingsSample.With(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
                TimeProvider.System,
                NullLogger.Instance
            );
            DmsOperationId operation = DmsOperationId.New();
            _outcomes.Add(
                await Record.ExceptionAsync(() =>
                    engine.CreateStagingAsync(
                        (IDmsTargetLockHolder)
                            RuntimeHelpers.GetUninitializedObject(typeof(MssqlTargetLockSession)),
                        operation.StagingDatabaseName,
                        new DmsProvisionClaim(operation, Guid.NewGuid()),
                        cancelled.Token
                    )
                )
            );
        }

        [Test]
        public void It_reports_each_as_not_dispatched() =>
            _outcomes.Should().HaveCount(5).And.AllBeOfType<DmsCommandNotDispatchedException>();
    }
}

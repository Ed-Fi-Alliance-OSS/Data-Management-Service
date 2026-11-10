// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.ProvisioningTestValues;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// The SQL Server side of the dispatch boundary without a server: the fence, the owned drop and the rename check
/// the token before their guarded batch is handed to the driver, and a cancellation found there is reported as not
/// dispatched. The connection is never opened, so nothing could have been sent.
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
        }

        [Test]
        public void It_reports_each_as_not_dispatched() =>
            _outcomes.Should().HaveCount(3).And.AllBeOfType<DmsCommandNotDispatchedException>();
    }
}

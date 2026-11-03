// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MssqlDataRepository = EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories.OpenIddictDataRepository;
using PostgresqlDataRepository = EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories.OpenIddictDataRepository;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// A token store only ever runs for a client that authenticated as approved or without an API
/// client row. Any other expected state is a caller's error, rejected before the store opens a
/// connection: each repository here points at a server that does not exist, so an attempt to
/// connect would surface as a connection failure instead.
/// </summary>
public class OpenIddictDataRepositoryStoreArgumentTests
{
    [TestFixture("postgresql", ApiClientApprovalState.Unapproved)]
    [TestFixture("postgresql", (ApiClientApprovalState)7)]
    [TestFixture("mssql", ApiClientApprovalState.Unapproved)]
    [TestFixture("mssql", (ApiClientApprovalState)7)]
    public class Given_A_Store_With_An_Expected_State_That_Is_Not_Admissible(
        string engine,
        ApiClientApprovalState expected
    ) : OpenIddictDataRepositoryStoreArgumentTests
    {
        private Exception? _exception;

        private static IOpenIddictDataRepository RepositoryFor(string engine)
        {
            IOptions<DatabaseOptions> options = Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = engine switch
                    {
                        "postgresql" =>
                            "Host=unreachable.invalid;Port=1;Timeout=1;Database=none;Username=none;Pooling=false",
                        "mssql" =>
                            "Server=unreachable.invalid,1;Database=none;User Id=none;Password=none;Connect Timeout=1;Pooling=false",
                        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null),
                    },
                    EncryptionKey = "unused-by-these-tests",
                }
            );
            return engine == "postgresql"
                ? new PostgresqlDataRepository(options, NullLogger<PostgresqlDataRepository>.Instance)
                : new MssqlDataRepository(options, NullLogger<MssqlDataRepository>.Instance);
        }

        [SetUp]
        public async Task Setup()
        {
            try
            {
                await RepositoryFor(engine)
                    .StoreTokenAsync(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        "subject",
                        DateTimeOffset.UtcNow.AddMinutes(30),
                        15,
                        "hashed-secret",
                        expected
                    );
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [Test]
        public void It_rejects_the_expected_state_argument() =>
            _exception
                .Should()
                .BeOfType<ArgumentOutOfRangeException>()
                .Which.ParamName.Should()
                .Be("expectedApiClientApproval");

        [Test]
        public void It_reports_the_rejected_value() =>
            _exception
                .Should()
                .BeOfType<ArgumentOutOfRangeException>()
                .Which.ActualValue.Should()
                .Be(expected);
    }
}

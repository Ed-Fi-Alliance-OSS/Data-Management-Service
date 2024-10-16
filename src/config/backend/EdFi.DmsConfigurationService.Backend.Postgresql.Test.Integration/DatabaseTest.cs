// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration;

public abstract class DatabaseTest : DatabaseTestBase
{
    protected NpgsqlConnection? Connection { get; set; }

    [SetUp]
    public async Task ConnectionSetup()
    {
        Connection = await DataSource!.OpenConnectionAsync();
    }

    [TearDown]
    public void ConnectionTeardown()
    {
        Connection?.Dispose();
    }
}

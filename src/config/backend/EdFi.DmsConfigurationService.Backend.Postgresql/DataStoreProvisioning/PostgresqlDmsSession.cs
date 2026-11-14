// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// A dedicated, unpooled connection bound to one verified PostgreSQL instance for its lifetime, with the
/// backend's <c>pid</c> and <c>backend_start</c> captured at open (D-7b). Only the engine sees the connection.
/// </summary>
internal sealed class PostgresqlDmsSession(
    NpgsqlConnection connection,
    DmsInstanceIdentity.Postgresql instance,
    DmsServerSessionId serverSession
) : DmsSession(instance, serverSession)
{
    public NpgsqlConnection Connection { get; } = connection;

    public override ValueTask DisposeAsync() => Connection.DisposeAsync();
}

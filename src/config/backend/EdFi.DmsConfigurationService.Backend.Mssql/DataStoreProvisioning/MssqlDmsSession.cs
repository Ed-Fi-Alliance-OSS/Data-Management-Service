// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// A dedicated, unpooled connection bound to one verified SQL Server instance for its lifetime, with the
/// session's <c>@@SPID</c> and <c>login_time</c> captured at open (D-7b). Only the engine sees the connection.
/// </summary>
internal sealed class MssqlDmsSession(
    SqlConnection connection,
    DmsInstanceIdentity.Mssql instance,
    DmsServerSessionId serverSession
) : DmsSession(instance, serverSession)
{
    public SqlConnection Connection { get; } = connection;

    public override ValueTask DisposeAsync() => Connection.DisposeAsync();
}

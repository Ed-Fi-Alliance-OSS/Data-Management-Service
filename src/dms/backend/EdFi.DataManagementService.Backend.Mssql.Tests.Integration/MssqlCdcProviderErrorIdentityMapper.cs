// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.Ddl;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal static class MssqlCdcProviderErrorIdentityMapper
{
    internal static CdcProviderErrorIdentity? MapProviderErrorIdentity(DbException exception)
    {
        if (exception is SqlException sqlException)
        {
            return new CdcProviderErrorIdentity(
                sqlException.Number.ToString(CultureInfo.InvariantCulture),
                sqlException.State.ToString(CultureInfo.InvariantCulture)
            );
        }

        return string.IsNullOrWhiteSpace(exception.SqlState)
            ? null
            : new CdcProviderErrorIdentity(exception.SqlState, null);
    }
}

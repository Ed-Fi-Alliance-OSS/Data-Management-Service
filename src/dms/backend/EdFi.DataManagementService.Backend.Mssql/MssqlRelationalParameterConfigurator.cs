// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External.Plans;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlRelationalParameterConfigurator : IRelationalParameterConfigurator
{
    public void ConfigureParameter(DbParameter dbParameter, QuerySqlParameter querySqlParameter)
    {
        ArgumentNullException.ThrowIfNull(dbParameter);
        ArgumentNullException.ThrowIfNull(querySqlParameter);

        if (querySqlParameter.Binding.Kind is not QuerySqlParameterBindingKind.MssqlStructured)
        {
            throw new NotSupportedException(
                $"SQL Server parameter configurator does not support binding kind '{querySqlParameter.Binding.Kind}'."
            );
        }

        if (dbParameter is not SqlParameter sqlParameter)
        {
            throw new InvalidOperationException(
                "SQL Server structured authorization parameter binding requires a SqlParameter instance."
            );
        }

        sqlParameter.SqlDbType = SqlDbType.Structured;
        sqlParameter.TypeName =
            querySqlParameter.Binding.StructuredTypeName
            ?? throw new InvalidOperationException(
                $"Structured binding for parameter '{querySqlParameter.ParameterName}' is missing a type name."
            );
    }
}

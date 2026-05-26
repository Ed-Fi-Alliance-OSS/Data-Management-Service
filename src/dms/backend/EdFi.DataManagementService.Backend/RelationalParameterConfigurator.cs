// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

public interface IRelationalParameterConfigurator
{
    void ConfigureParameter(DbParameter dbParameter, QuerySqlParameter querySqlParameter);
}

internal sealed class DefaultRelationalParameterConfigurator : IRelationalParameterConfigurator
{
    public static DefaultRelationalParameterConfigurator Instance { get; } = new();

    public void ConfigureParameter(DbParameter dbParameter, QuerySqlParameter querySqlParameter)
    {
        ArgumentNullException.ThrowIfNull(dbParameter);
        ArgumentNullException.ThrowIfNull(querySqlParameter);

        throw new NotSupportedException(
            $"Relational parameter binding kind '{querySqlParameter.Binding.Kind}' requires a provider-specific parameter configurator."
        );
    }
}

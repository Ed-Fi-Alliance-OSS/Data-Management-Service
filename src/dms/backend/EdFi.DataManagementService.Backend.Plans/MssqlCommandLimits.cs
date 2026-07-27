// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// SQL Server engine limits that constrain every command the backend emits, regardless of what the command
/// does. These are properties of the server and its client protocol, not of any one command shape, so both
/// read-side authorization budgeting and write-side batch sizing derive from them.
/// </summary>
public static class MssqlCommandLimits
{
    /// <summary>
    /// The maximum number of user parameters SQL Server can actually bind in one command. The documented
    /// RPC limit is 2100 parameters per request, but parameterized commands execute through
    /// <c>sp_executesql</c>, whose own <c>@stmt</c>/<c>@params</c> arguments consume two of those slots — a
    /// command carrying 2099 or 2100 user parameters is rejected by the server (error 8003). This is the
    /// single source of the usable-per-command ceiling: page-query authorization budgeting (see
    /// <see cref="AuthorizationParameterBudget.MssqlMaxCommandParameters"/>) and write-plan bulk insert
    /// batch sizing (see <see cref="PlanWriteBatchingConventions"/>) both derive from it. Do not reintroduce
    /// a per-consumer copy: those two ceilings previously drifted apart.
    /// </summary>
    public const int MaxUserParametersPerCommand = 2098;
}

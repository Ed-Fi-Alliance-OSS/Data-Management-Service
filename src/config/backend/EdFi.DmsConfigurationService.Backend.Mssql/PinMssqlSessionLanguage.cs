// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql;

/// <summary>
/// Pins the session language of the Configuration Service's SQL Server connection to us_english.
/// SQL Server localizes its system error messages to the session language, and
/// <see cref="MssqlExceptionExtensions"/> reads the violated constraint from where the us_english
/// messages quote it, so a session in another language would turn an expected constraint violation
/// into an unclassified failure (DMS-1529).
/// </summary>
/// <remarks>
/// Every SQL Server connection the Configuration Service opens (the repositories, the OpenIddict
/// stores, the job and lock sessions, the startup deploy) is opened from
/// <see cref="DatabaseOptions.DatabaseConnection"/>, so the language is pinned once, on the options,
/// overriding any <c>Current Language</c> the configured connection string sets and the login's
/// default language. A blank connection string is left for <see cref="DatabaseOptionsValidator"/> to
/// reject, and one that cannot be parsed is left unchanged: no connection can be opened from it
/// either.
/// </remarks>
internal sealed class PinMssqlSessionLanguage : IPostConfigureOptions<DatabaseOptions>
{
    public const string SessionLanguage = "us_english";

    public void PostConfigure(string? name, DatabaseOptions options) =>
        options.DatabaseConnection = Pin(options.DatabaseConnection);

    internal static string Pin(string databaseConnection)
    {
        if (string.IsNullOrWhiteSpace(databaseConnection))
        {
            return databaseConnection;
        }

        try
        {
            return new SqlConnectionStringBuilder(databaseConnection)
            {
                CurrentLanguage = SessionLanguage,
            }.ConnectionString;
        }
        catch (ArgumentException)
        {
            return databaseConnection;
        }
    }
}

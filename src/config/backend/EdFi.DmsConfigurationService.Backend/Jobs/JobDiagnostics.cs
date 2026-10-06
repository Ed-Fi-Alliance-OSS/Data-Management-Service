// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Provider-neutral helpers for spec D-16a. Provider projects add the provider error code
/// (<c>PostgresqlJobDiagnostics</c>, <c>MssqlJobDiagnostics</c>); this class has no provider knowledge.
/// </summary>
public static class JobDiagnostics
{
    /// <summary>
    /// The <c>/</c>-joined full type names of <paramref name="exception"/> and each inner exception, outermost
    /// first. Only type names are read: no message, data, or stack trace.
    /// </summary>
    public static string TypeChain(Exception exception)
    {
        List<string> typeNames = [];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            typeNames.Add(current.GetType().FullName ?? current.GetType().Name);
        }
        return string.Join('/', typeNames);
    }

    /// <summary>The diagnostic for a failure, with an optional provider error code.</summary>
    public static JobFailureDiagnostic From(
        Exception exception,
        string operation,
        string? providerErrorCode = null
    ) => new(TypeChain(exception), providerErrorCode, operation);

    /// <summary>
    /// An identifier read from a row (<c>JobId</c>, <c>JobType</c>, <c>LeaseOwner</c>, <c>ScheduleType</c>)
    /// made safe for a log field by the shared sanitizer.
    /// </summary>
    public static string SafeIdentifier(string? identifier) => LoggingUtility.SanitizeForLog(identifier);
}

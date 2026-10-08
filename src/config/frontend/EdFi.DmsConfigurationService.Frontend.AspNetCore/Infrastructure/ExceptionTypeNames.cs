// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>Diagnostics that read an exception's type names and nothing else.</summary>
internal static class ExceptionTypeNames
{
    /// <summary>
    /// The full type names of an exception and each inner exception, outermost first, joined with
    /// <c>-&gt;</c>. No message, data or stack trace is read.
    /// </summary>
    public static string Chain(Exception exception)
    {
        List<string> names = [];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            names.Add(current.GetType().FullName ?? current.GetType().Name);
        }

        return string.Join(" -> ", names);
    }
}

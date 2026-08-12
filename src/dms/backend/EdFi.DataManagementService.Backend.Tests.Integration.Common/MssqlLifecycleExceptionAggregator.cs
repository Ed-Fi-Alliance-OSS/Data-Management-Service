// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

internal static class MssqlLifecycleExceptionAggregator
{
    [DoesNotReturn]
    public static void Throw(Exception primaryException, IEnumerable<Exception> cleanupExceptions)
    {
        ArgumentNullException.ThrowIfNull(primaryException);
        ArgumentNullException.ThrowIfNull(cleanupExceptions);

        Exception[] secondaryExceptions = [.. cleanupExceptions];
        if (secondaryExceptions.Length == 0)
        {
            ExceptionDispatchInfo.Capture(primaryException).Throw();
        }

        throw new AggregateException(
            "A SQL Server lifecycle operation failed and one or more cleanup operations also failed.",
            (IEnumerable<Exception>)[primaryException, .. secondaryExceptions]
        );
    }

    [DoesNotReturn]
    public static void Throw(IReadOnlyList<Exception> exceptions)
    {
        ArgumentNullException.ThrowIfNull(exceptions);

        if (exceptions.Count == 0)
        {
            throw new ArgumentException("At least one exception is required.", nameof(exceptions));
        }

        if (exceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
        }

        throw new AggregateException("Multiple SQL Server lifecycle cleanup operations failed.", exceptions);
    }
}

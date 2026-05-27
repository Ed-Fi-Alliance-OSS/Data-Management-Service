// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.TestHelpers;

public static class LoggerExtensions
{
    public static void VerifyLogError<T>(this ILogger<T> logger, string expectedMessage)
    {
        A.CallTo(logger)
            .Where(call =>
                call.Method.Name == "Log"
                && call.Arguments.Get<LogLevel>(0) == LogLevel.Error
                && call.Arguments.Get<object>(2)!.ToString()!.Contains(expectedMessage)
            )
            .MustHaveHappened();
    }
}

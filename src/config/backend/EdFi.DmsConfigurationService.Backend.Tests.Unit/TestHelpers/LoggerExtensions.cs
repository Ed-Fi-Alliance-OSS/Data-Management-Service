// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.TestHelpers;

public static class LoggerExtensions
{
    public static void VerifyLogError<T>(this ILogger<T> logger, string expectedMessage) =>
        logger.VerifyLog(LogLevel.Error, expectedMessage);

    /// <summary>
    /// Asserts that the logger recorded an entry at exactly this level whose formatted message
    /// carries the expected text. The level matters where a contract distinguishes a routine
    /// outcome from one an operator has to act on.
    /// </summary>
    public static void VerifyLog<T>(this ILogger<T> logger, LogLevel level, string expectedMessage)
    {
        A.CallTo(logger).Where(call => IsLogEntry(call, level, expectedMessage)).MustHaveHappened();
    }

    /// <summary>
    /// Asserts that no entry at this level carries the text. Scope the text to the event under
    /// test: unrelated diagnostics from reused collaborators are expected and are not failures.
    /// </summary>
    public static void VerifyNoLog<T>(this ILogger<T> logger, LogLevel level, string expectedMessage)
    {
        A.CallTo(logger).Where(call => IsLogEntry(call, level, expectedMessage)).MustNotHaveHappened();
    }

    /// <summary>
    /// Every formatted message the logger recorded, for assertions about message content.
    /// </summary>
    public static IReadOnlyList<string> LoggedMessages<T>(this ILogger<T> logger) =>
        [
            .. Fake.GetCalls(logger)
                .Where(call => call.Method.Name == "Log")
                .Select(call => call.Arguments.Get<object>(2)?.ToString() ?? string.Empty),
        ];

    /// <summary>
    /// Every recorded entry as its formatted message together with the text of any exception
    /// logged alongside it. An exception travels as its own argument, so a message-only view
    /// cannot support a claim about what no entry may contain.
    /// </summary>
    public static IReadOnlyList<string> LoggedEntryTexts<T>(this ILogger<T> logger) =>
        [
            .. Fake.GetCalls(logger)
                .Where(call => call.Method.Name == "Log")
                .Select(call => $"{call.Arguments.Get<object>(2)} {call.Arguments.Get<Exception>(3)}"),
        ];

    private static bool IsLogEntry(
        FakeItEasy.Core.IFakeObjectCall call,
        LogLevel level,
        string expectedMessage
    ) =>
        call.Method.Name == "Log"
        && call.Arguments.Get<LogLevel>(0) == level
        && call.Arguments.Get<object>(2)!.ToString()!.Contains(expectedMessage);
}

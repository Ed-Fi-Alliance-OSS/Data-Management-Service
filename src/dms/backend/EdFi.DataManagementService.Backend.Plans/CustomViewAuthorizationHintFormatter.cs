// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Builds the authorization failure hint auth.md §"Authorization Failure Hints" specifies for a custom
/// view-based strategy: <c>You may need {a/an} {Display Text}.</c>, for example
/// <c>You may need a Student with CTE Course Enrollments.</c> for
/// <c>StudentWithCTECourseEnrollments</c>.
/// </summary>
/// <remarks>
/// The built-in relationship auth views carry fixed hint strings on their auth-object definitions. A custom
/// view has no such definition — it is configured in CMS after the schema was generated — so its hint has to
/// be derived from the strategy name itself.
/// <para>
/// The returned text carries no <c>Hint:</c> prefix. Callers compose it into the ProblemDetails
/// <c>detail</c>, which supplies that prefix, exactly as the relationship auth-object hints do.
/// </para>
/// </remarks>
public static class CustomViewAuthorizationHintFormatter
{
    private const string WithToken = "With";

    /// <summary>
    /// Formats the hint sentence for <paramref name="strategyName"/>.
    /// </summary>
    public static string Format(string strategyName)
    {
        var displayText = FormatDisplayText(strategyName);

        return $"You may need {SelectArticle(displayText)} {displayText}.";
    }

    /// <summary>
    /// Converts a <c>{BasisResource}With{SomeDescription}</c> strategy name into its display text by
    /// splitting on camel-case boundaries and lowercasing the <c>With</c> separator:
    /// <c>StudentWithCTECourseEnrollments</c> becomes <c>Student with CTE Course Enrollments</c>.
    /// </summary>
    /// <remarks>
    /// Acronym runs survive as single words (<c>CTE</c>, not <c>C T E</c>). Every token that is exactly
    /// <c>With</c> is lowercased, not only the convention's separator, so a description that itself contains
    /// <c>With</c> still reads as prose.
    /// </remarks>
    public static string FormatDisplayText(string strategyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);

        var tokens = SplitCamelCaseTokens(strategyName);

        return string.Join(
            ' ',
            tokens.Select(static token =>
                string.Equals(token, WithToken, StringComparison.Ordinal)
                    ? token.ToLower(CultureInfo.InvariantCulture)
                    : token
            )
        );
    }

    /// <summary>
    /// Splits on camel-case boundaries while keeping acronym runs intact. A boundary opens before an
    /// uppercase letter that follows a lowercase letter or a digit, and before the last uppercase letter of
    /// a run that is followed by a lowercase letter — which is what keeps <c>CTECourse</c> as
    /// <c>CTE</c> + <c>Course</c> rather than <c>CTEC</c> + <c>ourse</c>.
    /// </summary>
    private static List<string> SplitCamelCaseTokens(string value)
    {
        List<string> tokens = [];
        var currentToken = new StringBuilder();

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (currentToken.Length > 0 && IsTokenBoundary(value, index))
            {
                tokens.Add(currentToken.ToString());
                currentToken.Clear();
            }

            currentToken.Append(character);
        }

        if (currentToken.Length > 0)
        {
            tokens.Add(currentToken.ToString());
        }

        return tokens;
    }

    private static bool IsTokenBoundary(string value, int index)
    {
        if (!char.IsUpper(value[index]))
        {
            return false;
        }

        var previousCharacter = value[index - 1];

        if (char.IsLower(previousCharacter) || char.IsDigit(previousCharacter))
        {
            return true;
        }

        // Closing an acronym run: the current uppercase letter starts the next word when a lowercase
        // letter follows it.
        return char.IsUpper(previousCharacter) && index + 1 < value.Length && char.IsLower(value[index + 1]);
    }

    private static string SelectArticle(string displayText) =>
        displayText.Length > 0 && "AEIOU".Contains(char.ToUpperInvariant(displayText[0])) ? "an" : "a";
}

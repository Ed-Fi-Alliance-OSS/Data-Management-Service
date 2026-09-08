// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Renders externally supplied values for inclusion in a loader message.
/// </summary>
/// <remarks>
/// Loader messages quote configuration entries and filesystem paths, both of which arrive from
/// outside the process, and those messages are written to a line-oriented diagnostic channel and are
/// re-emitted through the host's logger afterwards. A value carrying a line break would forge a line
/// on that channel and in the log, so control characters are escaped rather than reproduced. The
/// escaping is applied at the point a value enters a message, so no caller has to remember to do it.
/// </remarks>
internal static class PluginDiagnosticText
{
    /// <summary>
    /// Returns <paramref name="value"/> with every control character replaced by a printable escape.
    /// </summary>
    internal static string Quote(string value)
    {
        if (!value.Any(char.IsControl))
        {
            return value;
        }

        StringBuilder escaped = new(value.Length + 8);

        foreach (char character in value)
        {
            switch (character)
            {
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        escaped.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
                    }
                    else
                    {
                        escaped.Append(character);
                    }

                    break;
            }
        }

        return escaped.ToString();
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using CmsHierarchy;
using CmsHierarchy.Extensions;
using CmsHierarchy.Model;

// Example usage: dotnet run --no-launch-profile --command ParseXml --input input.xml --output output.json --outputFormat ToFile
// Example usage: dotnet run --no-launch-profile --command Transform --input input1.json;input2.json --output output.json --outputFormat ToFile

// Example usage: dotnet run --no-launch-profile --command ParseXml --input input.xml --outputFormat Json
// Example usage: dotnet run --no-launch-profile --command Transform --input input1.json;input2.json --outputFormat Json

if (args.Length < 4)
{
    Console.WriteLine(
        "Usage: --command <ParseXml|Transform> --input <inputFilePath> --output <outputFilePath> --outputFormat <ToFile|Json>"
    );
    return;
}

string command = string.Empty;
string input = string.Empty;
string output = string.Empty;
string outputFormat = string.Empty;
string skipAuthorizations = string.Empty;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--command":
            command = args[++i];
            break;
        case "--input":
            input = args[++i];
            break;
        case "--output":
            output = args[++i];
            break;
        case "--outputFormat":
            outputFormat = args[++i];
            break;
        case "--skipAuths":
            skipAuthorizations = args[++i];
            break;
    }
}

if (
    string.IsNullOrEmpty(command)
    || string.IsNullOrEmpty(input)
    || string.IsNullOrEmpty(outputFormat)
    || (
        outputFormat.Equals("ToFile", StringComparison.InvariantCultureIgnoreCase)
        && string.IsNullOrEmpty(output)
    )
)
{
    Console.WriteLine(
        "Please provide valid command, input, output (if outputFormat is ToFile), and output format."
    );
    return;
}

string resultJson;
var jsonSerializerOptions = new JsonSerializerOptions
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
};

switch (command)
{
    case "ParseXml":
        Claim[] claims = XmlToClaimsParser.ParseXml(input);
        resultJson = JsonSerializer.Serialize(claims, jsonSerializerOptions);
        break;

    case "Transform":
        var claimsFromJson = ClaimSetToAuthHierarchy.GetBaseClaimHierarchy();
        var jsonFiles = input.Split(';');
        foreach (var filePath in jsonFiles)
        {
            claimsFromJson = ClaimSetToAuthHierarchy.TransformClaims(filePath, claimsFromJson);
        }

        if (!string.IsNullOrEmpty(skipAuthorizations))
        {
            claimsFromJson.RemoveAuthorizationStrategies(skipAuthorizations.Split(';'));
        }

        resultJson = JsonSerializer.Serialize(claimsFromJson, jsonSerializerOptions);
        break;

    default:
        Console.WriteLine("Unknown command. Please use ParseXml or Transform.");
        return;
}

if (outputFormat.Equals("ToFile", StringComparison.InvariantCultureIgnoreCase))
{
    if (string.IsNullOrEmpty(output))
    {
        Console.WriteLine("Please provide the output file path.");
        return;
    }
    File.WriteAllText(output, resultJson);
}
else if (outputFormat.Equals("Json", StringComparison.InvariantCultureIgnoreCase))
{
    Console.WriteLine(resultJson);
}
else
{
    Console.WriteLine("Unknown output format. Please use ToFile or Json.");
}

using System.Text.Json.Nodes;
using Json.Path;
using Microsoft.Extensions.Logging;

namespace DmsOpenApiGenerator.Services;

internal static class JsonHelperExtensions
{
    public static T? SelectNodeFromPathAs<T>(this JsonNode jsonNode, string jsonPathString, ILogger logger)
    {
        JsonNode? selectedNode = SelectNodeFromPath(jsonNode, jsonPathString, logger);

        if (selectedNode == null)
        {
            return default;
        }

        JsonValue resultNode =
            selectedNode.AsValue() ?? throw new InvalidOperationException("Unexpected JSONPath value error");
        return resultNode.GetValue<T>();
    }

    public static JsonNode? SelectNodeFromPath(this JsonNode jsonNode, string jsonPathString, ILogger logger)
    {
        try
        {
            JsonPath? jsonPath = JsonPath.Parse(jsonPathString);
            if (jsonPath == null)
            {
                logger.LogError("Malformed JSONPath string '{JsonPathString}'", jsonPathString);
                throw new InvalidOperationException($"Malformed JSONPath string '{jsonPathString}'");
            }

            PathResult result = jsonPath.Evaluate(jsonNode);

            if (result.Matches == null)
            {
                logger.LogError("Malformed JSONPath string '{JsonPathString}'", jsonPathString);
                throw new InvalidOperationException($"Unexpected Json.Path error for '{jsonPathString}'");
            }

            try
            {
                if (result.Matches.Count == 0)
                {
                    return null;
                }
            }
            catch (ArgumentException ae)
            {
                throw new InvalidOperationException(
                    "JSON value to be parsed is problematic, for example might contain duplicate keys.",
                    ae
                );
            }

            if (result.Matches.Count != 1)
            {
                logger.LogError(
                    "JSONPath string '{JsonPathString}' selected multiple values",
                    jsonPathString
                );
                throw new InvalidOperationException(
                    $"JSONPath string '{jsonPathString}' selected multiple values"
                );
            }

            return result.Matches[0].Value;
        }
        catch (PathParseException ex)
        {
            logger.LogError(ex, "PathParseException for JSONPath string '{JsonPathString}'", jsonPathString);
            throw new InvalidOperationException($"Unexpected Json.Path error for '{jsonPathString}'");
        }
    }

    public static T SelectRequiredNodeFromPathAs<T>(
        this JsonNode jsonNode,
        string jsonPathString,
        ILogger logger
    )
    {
        var result =
            SelectNodeFromPathAs<T>(jsonNode, jsonPathString, logger)
            ?? throw new InvalidOperationException($"Node at path '{jsonPathString}' not found");
        return result;
    }

    public static JsonNode SelectRequiredNodeFromPath(
        this JsonNode jsonNode,
        string jsonPathString,
        ILogger logger
    )
    {
        return SelectNodeFromPath(jsonNode, jsonPathString, logger)
            ?? throw new InvalidOperationException($"Node at path '{jsonPathString}' not found");
    }
}

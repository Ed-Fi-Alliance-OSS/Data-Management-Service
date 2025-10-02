// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Tests.E2E.Builders;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

[Binding]
public class SyntheticSchemaStepDefinitions(
    ScenarioContext scenarioContext,
    PlaywrightContext playwrightContext,
    TestLogger logger
)
{
    private readonly ScenarioContext _scenarioContext = scenarioContext;
    private readonly PlaywrightContext _playwrightContext = playwrightContext;
    private readonly TestLogger _logger = logger;
    private ApiSchemaBuilder? _currentSchemaBuilder;

    // Cached JsonSerializerOptions for performance
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Given(@"a synthetic (?:core|extension) schema with project ""(.*)""")]
    public void GivenASyntheticSchemaWithProject(string projectName)
    {
        _currentSchemaBuilder = new ApiSchemaBuilder().WithStartProject(projectName);
        _scenarioContext["currentSchemaBuilder"] = _currentSchemaBuilder;
    }

    [Given(@"the schema has resource ""(.*)"" with identity ""(.*)""")]
    public void GivenTheSchemaHasResourceWithIdentity(string resourceName, string identityProperty)
    {
        _currentSchemaBuilder = _scenarioContext.Get<ApiSchemaBuilder>("currentSchemaBuilder");

        // Determine additional properties based on resource type
        var schemaProperties = new List<(string, string)> { (identityProperty, "string") };

        switch (resourceName)
        {
            case "Student":
                schemaProperties.AddRange([("firstName", "string"), ("lastSurname", "string")]);
                break;
            case "Staff":
                schemaProperties.AddRange([("firstName", "string"), ("lastSurname", "string")]);
                break;
            case "Teacher":
                schemaProperties.AddRange([("firstName", "string"), ("lastSurname", "string")]);
                break;
            case "School":
                schemaProperties.Add(("nameOfInstitution", "string"));
                break;
            case "District":
                schemaProperties.Add(("nameOfInstitution", "string"));
                break;
            case "Section":
                schemaProperties.Add(("sectionName", "string"));
                break;
        }

        _currentSchemaBuilder
            .WithStartResource(resourceName)
            .WithIdentityJsonPaths($"$.{identityProperty}")
            .WithSimpleJsonSchema(schemaProperties.ToArray())
            .WithEndResource();
    }

    [Given(@"the schema has resource ""(.*)"" with identities")]
    public void GivenTheSchemaHasResourceWithIdentities(string resourceName, Table table)
    {
        _currentSchemaBuilder = _scenarioContext.Get<ApiSchemaBuilder>("currentSchemaBuilder");

        var identityPaths = table.Rows.Select(row => $"$.{row["identity"]}").ToArray();
        var schemaProperties = table.Rows.Select(row => (row["identity"], "string")).ToArray();

        _currentSchemaBuilder
            .WithStartResource(resourceName)
            .WithIdentityJsonPaths(identityPaths)
            .WithSimpleJsonSchema(schemaProperties);

        // For association resources, add references based on the identities
        if (resourceName.Contains("Association"))
        {
            _currentSchemaBuilder.WithStartDocumentPathsMapping();

            foreach (var row in table.Rows)
            {
                var identity = row["identity"];

                // Determine the referenced resource name from the identity
                // e.g., "studentUniqueId" -> "Student", "schoolId" -> "School"
                if (identity.EndsWith("UniqueId"))
                {
                    var referencedResource = identity[..^8]; // Remove "UniqueId"
                    referencedResource = char.ToUpper(referencedResource[0]) + referencedResource[1..];
                    _currentSchemaBuilder.WithSimpleReference(referencedResource, identity);
                }
                else if (identity.EndsWith("Id"))
                {
                    var referencedResource = identity[..^2]; // Remove "Id"
                    referencedResource = char.ToUpper(referencedResource[0]) + referencedResource[1..];
                    _currentSchemaBuilder.WithSimpleReference(referencedResource, identity);
                }
            }

            _currentSchemaBuilder.WithEndDocumentPathsMapping();

            // Add common association properties
            var associationProperties = schemaProperties.ToList();
            if (resourceName == "StudentSchoolAssociation")
            {
                associationProperties.Add(("entryDate", "string"));
            }

            _currentSchemaBuilder.WithSimpleJsonSchema(associationProperties.ToArray());
        }

        _currentSchemaBuilder.WithEndResource();
    }

    [Given(@"the schema has descriptor resource ""(.*)""")]
    public void GivenTheSchemaHasDescriptorResource(string descriptorName)
    {
        _currentSchemaBuilder = _scenarioContext.Get<ApiSchemaBuilder>("currentSchemaBuilder");

        _currentSchemaBuilder
            .WithStartResource(descriptorName, isDescriptor: true)
            .WithIdentityJsonPaths($"$.{char.ToLower(descriptorName[0]) + descriptorName[1..]}Id")
            .WithSimpleJsonSchema(
                ($"{char.ToLower(descriptorName[0]) + descriptorName[1..]}Id", "string"),
                ("codeValue", "string"),
                ("shortDescription", "string"),
                ("description", "string"),
                ("namespace", "string")
            )
            .WithEndResource();
    }

    [Given(@"the ""(.*)"" resource has reference ""(.*)"" to ""(.*)""")]
    public void GivenTheResourceHasReferenceTo(
        string resourceName,
        string referenceName,
        string targetResourceName
    )
    {
        _currentSchemaBuilder = _scenarioContext.Get<ApiSchemaBuilder>("currentSchemaBuilder");

        // Determine the identity property based on target resource
        var identityProperty = targetResourceName switch
        {
            "GradingPeriod" => "gradingPeriodName",
            "School" => "schoolId",
            "District" => "districtId",
            "Student" => "studentUniqueId",
            "Staff" => "staffUniqueId",
            "Teacher" => "teacherId",
            _ => char.ToLower(targetResourceName[0]) + targetResourceName[1..] + "Id",
        };

        // Determine the identity property for current resource
        var currentResourceIdentity = resourceName switch
        {
            "Student" => "studentUniqueId",
            "Staff" => "staffUniqueId",
            "Teacher" => "teacherId",
            "School" => "schoolId",
            "District" => "districtId",
            "Section" => "sectionId",
            _ => char.ToLower(resourceName[0]) + resourceName[1..] + "Id",
        };

        // Determine additional properties based on resource type
        var additionalProperties = new List<(string, string)>();

        switch (resourceName)
        {
            case "Student":
                additionalProperties.AddRange([("firstName", "string"), ("lastSurname", "string")]);
                break;
            case "Staff":
                additionalProperties.AddRange([("firstName", "string"), ("lastSurname", "string")]);
                break;
            case "Teacher":
                additionalProperties.AddRange([("firstName", "string"), ("lastSurname", "string")]);
                break;
            case "School":
                additionalProperties.Add(("nameOfInstitution", "string"));
                break;
            case "District":
                additionalProperties.Add(("nameOfInstitution", "string"));
                break;
            case "Section":
                additionalProperties.Add(("sectionName", "string"));
                break;
        }

        // Build the schema properties list
        var schemaProperties = new List<(string, string)> { (currentResourceIdentity, "string") };
        schemaProperties.AddRange(additionalProperties);
        schemaProperties.Add((referenceName, "object"));

        // Rebuild the resource with the reference
        _currentSchemaBuilder
            .WithStartResource(resourceName)
            .WithIdentityJsonPaths($"$.{currentResourceIdentity}")
            .WithSimpleJsonSchema(schemaProperties.ToArray())
            .WithStartDocumentPathsMapping()
            .WithSimpleReference(targetResourceName, identityProperty)
            .WithEndDocumentPathsMapping()
            .WithEndResource();
    }

    [Given(@"the ""(.*)"" resource has properties")]
    public void GivenTheResourceHasProperties(string resourceName, Table table)
    {
        // Store additional properties to be added to resources
        if (!_scenarioContext.ContainsKey("resourceProperties"))
        {
            _scenarioContext["resourceProperties"] =
                new Dictionary<string, List<(string name, string type)>>();
        }

        var properties = _scenarioContext.Get<Dictionary<string, List<(string, string)>>>(
            "resourceProperties"
        );
        properties[resourceName] = table.Rows.Select(row => (row["propertyName"], row["type"])).ToList();
    }

    [Given(@"the ""(.*)"" resource has array uniqueness constraint on ""(.*)""")]
    public void GivenTheResourceHasArrayUniquenessConstraintOn(string resourceName, string arrayPath)
    {
        if (!_scenarioContext.ContainsKey("arrayConstraints"))
        {
            _scenarioContext["arrayConstraints"] = new Dictionary<string, List<string>>();
        }

        var constraints = _scenarioContext.Get<Dictionary<string, List<string>>>("arrayConstraints");
        if (!constraints.ContainsKey(resourceName))
        {
            constraints[resourceName] = new List<string>();
        }
        constraints[resourceName].Add(arrayPath);
    }

    [When(@"the schema is deployed to the DMS")]
    public async Task WhenTheSchemaIsDeployedToTheDMS()
    {
        _currentSchemaBuilder = _scenarioContext.Get<ApiSchemaBuilder>("currentSchemaBuilder");

        // End the current project
        _currentSchemaBuilder.WithEndProject();

        // Generate upload request
        var uploadRequest = _currentSchemaBuilder.GenerateUploadRequest();

        _logger.log.Information("Uploading synthetic API schemas to DMS");
        _logger.log.Information("Core schema: {Schema}", uploadRequest.CoreSchema);

        // Call the upload endpoint
        var apiContext = _playwrightContext.ApiRequestContext!;
        var uploadUrl = $"{_playwrightContext.ApiUrl.TrimEnd('/')}/management/upload-api-schema";

        _logger.log.Information($"Calling schema upload endpoint at {uploadUrl}");

        var response = await apiContext.PostAsync(
            uploadUrl,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                Data = JsonSerializer.Serialize(uploadRequest),
            }
        );

        var responseText = await response.TextAsync();
        _logger.log.Information(
            "Upload response status: {Status}, Body: {Body}",
            response.Status,
            responseText
        );

        if (!response.Ok)
        {
            throw new Exception($"Failed to upload schema. Status: {response.Status}, Body: {responseText}");
        }

        // Parse the response as a simple JSON object since the API returns a different format
        var responseJson = JsonSerializer.Deserialize<JsonElement>(responseText, _jsonOptions);

        // Check if there's an error field
        if (responseJson.TryGetProperty("error", out var errorElement))
        {
            var errorMessage = errorElement.GetString() ?? "Unknown error";

            // Check for detailed failures
            if (
                responseJson.TryGetProperty("failures", out var failuresElement)
                && failuresElement.ValueKind == JsonValueKind.Array
            )
            {
                var failures = new List<string>();
                foreach (var failure in failuresElement.EnumerateArray())
                {
                    if (
                        failure.TryGetProperty("type", out var typeElem)
                        && failure.TryGetProperty("message", out var msgElem)
                    )
                    {
                        failures.Add($"{typeElem.GetString()}: {msgElem.GetString()}");
                    }
                }
                if (failures.Count > 0)
                {
                    errorMessage = string.Join("; ", failures);
                }
            }

            throw new Exception($"Schema upload failed: {errorMessage}");
        }

        // Extract success response fields
        var reloadId = responseJson.TryGetProperty("reloadId", out var reloadIdElem)
            ? reloadIdElem.GetString()
            : null;
        var schemasProcessed = responseJson.TryGetProperty("schemasProcessed", out var schemasElem)
            ? schemasElem.GetInt32()
            : 0;

        if (string.IsNullOrEmpty(reloadId))
        {
            _logger.log.Warning("Schema upload succeeded but ReloadId was not provided");
        }

        _logger.log.Information(
            "Schema upload completed successfully. Processed {Count} schemas with reload ID {ReloadId}",
            schemasProcessed,
            reloadId ?? "N/A"
        );

        // Increased delay to ensure schema is fully loaded
        await Task.Delay(1000);
    }

    [Given(@"the schema is deployed")]
    public async Task GivenTheSchemaIsDeployed()
    {
        await WhenTheSchemaIsDeployedToTheDMS();
    }

    [When(@"the schema upload fails")]
    public async Task WhenTheSchemaUploadFails()
    {
        _currentSchemaBuilder = _scenarioContext.Get<ApiSchemaBuilder>("currentSchemaBuilder");

        // End the current project
        _currentSchemaBuilder.WithEndProject();

        // Generate upload request
        var uploadRequest = _currentSchemaBuilder.GenerateUploadRequest();

        _logger.log.Information("Uploading synthetic API schemas to DMS (expecting failure)");

        // Call the upload endpoint
        var apiContext = _playwrightContext.ApiRequestContext!;
        var uploadUrl = $"{_playwrightContext.ApiUrl.TrimEnd('/')}/management/upload-api-schema";

        var response = await apiContext.PostAsync(
            uploadUrl,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                Data = JsonSerializer.Serialize(uploadRequest),
            }
        );

        // Store the response for validation
        _scenarioContext["schemaUploadResponse"] = response;
        _scenarioContext["schemaUploadResponseText"] = await response.TextAsync();
    }

    [Then(@"the schema upload response should be")]
    public void ThenTheSchemaUploadResponseShouldBe(string expectedResponse)
    {
        var responseText = _scenarioContext.Get<string>("schemaUploadResponseText");

        var actualResponse = JsonSerializer.Deserialize<UploadSchemaResponse>(responseText, _jsonOptions);

        var expectedResponseObj = JsonSerializer.Deserialize<UploadSchemaResponse>(
            expectedResponse,
            _jsonOptions
        );

        actualResponse.Should().BeEquivalentTo(expectedResponseObj);
    }

    [Then(@"the schema upload response should indicate success")]
    public void ThenTheSchemaUploadResponseShouldIndicateSuccess()
    {
        var responseText = _scenarioContext.Get<string>("schemaUploadResponseText");

        var response = JsonSerializer.Deserialize<UploadSchemaResponse>(responseText, _jsonOptions);

        response.Should().NotBeNull();
        response!.Success.Should().BeTrue("Schema upload should have succeeded");
        response.SchemasProcessed.Should().BeGreaterThan(0);
        response.ReloadId.Should().NotBeNull();
    }

    [Then(@"the schema upload response should indicate failure with message ""(.*)""")]
    public void ThenTheSchemaUploadResponseShouldIndicateFailureWithMessage(string expectedMessage)
    {
        var responseText = _scenarioContext.Get<string>("schemaUploadResponseText");

        var response = JsonSerializer.Deserialize<UploadSchemaResponse>(responseText, _jsonOptions);

        response.Should().NotBeNull();
        response!.Success.Should().BeFalse("Schema upload should have failed");
        response.ErrorMessage.Should().Contain(expectedMessage);
    }

    [Then(@"the schema upload response should contain failures")]
    public void ThenTheSchemaUploadResponseShouldContainFailures(Table table)
    {
        var responseText = _scenarioContext.Get<string>("schemaUploadResponseText");

        var response = JsonSerializer.Deserialize<UploadSchemaResponse>(responseText, _jsonOptions);

        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.Failures.Should().NotBeNull();
        response.Failures!.Count.Should().Be(table.RowCount);

        for (int i = 0; i < table.RowCount; i++)
        {
            var expectedFailure = table.Rows[i];
            var actualFailure = response.Failures[i];

            if (expectedFailure.ContainsKey("FailureType"))
            {
                actualFailure.FailureType.Should().Be(expectedFailure["FailureType"]);
            }

            if (expectedFailure.ContainsKey("Message"))
            {
                actualFailure.Message.Should().Contain(expectedFailure["Message"]);
            }
        }
    }

    [Then(@"the schema upload response status should be (\d+)")]
    public void ThenTheSchemaUploadResponseStatusShouldBe(int expectedStatus)
    {
        var response = _scenarioContext.Get<IAPIResponse>("schemaUploadResponse");
        response.Status.Should().Be(expectedStatus);
    }
}

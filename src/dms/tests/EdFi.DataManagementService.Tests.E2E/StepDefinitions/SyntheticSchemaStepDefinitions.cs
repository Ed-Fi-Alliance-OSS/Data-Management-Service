// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Tests.E2E.Builders;
using EdFi.DataManagementService.Tests.E2E.Management;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

[Binding]
public class SyntheticSchemaStepDefinitions
{
    private readonly ScenarioContext _scenarioContext;
    private readonly PlaywrightContext _playwrightContext;
    private readonly TestLogger _logger;
    private ApiSchemaBuilder? _currentSchemaBuilder;

    public SyntheticSchemaStepDefinitions(
        ScenarioContext scenarioContext,
        PlaywrightContext playwrightContext,
        TestLogger logger
    )
    {
        _scenarioContext = scenarioContext;
        _playwrightContext = playwrightContext;
        _logger = logger;
    }

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

        _currentSchemaBuilder
            .WithStartResource(resourceName)
            .WithIdentityJsonPaths($"$.{identityProperty}")
            .WithSimpleJsonSchema((identityProperty, "string"))
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
            .WithSimpleJsonSchema(schemaProperties)
            .WithEndResource();
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

        // We need to rebuild the resource with the reference
        // First, get the identity property name - assume it's the lowercase first letter + rest of targetResourceName + "Id"
        var identityProperty = char.ToLower(targetResourceName[0]) + targetResourceName[1..] + "Id";

        // Rebuild the resource with the reference
        _currentSchemaBuilder
            .WithStartResource(resourceName)
            .WithIdentityJsonPaths($"$.{char.ToLower(resourceName[0]) + resourceName[1..] + "UniqueId"}")
            .WithSimpleJsonSchema(
                (char.ToLower(resourceName[0]) + resourceName[1..] + "UniqueId", "string"),
                (referenceName, "object")
            )
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

        // Call the upload endpoint
        var apiContext = _playwrightContext.ApiRequestContext!;
        var uploadUrl = $"{_playwrightContext.ApiUrl.TrimEnd('/')}/management/upload-and-reload-schema";

        _logger.log.Information($"Calling schema upload endpoint at {uploadUrl}");

        var response = await apiContext.PostAsync(
            uploadUrl,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                Data = JsonSerializer.Serialize(uploadRequest),
            }
        );

        if (!response.Ok)
        {
            var responseBody = await response.TextAsync();
            throw new Exception($"Failed to upload schema. Status: {response.Status}, Body: {responseBody}");
        }

        var responseText = await response.TextAsync();
        var uploadResponse = JsonSerializer.Deserialize<UploadSchemaResponse>(
            responseText,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        if (uploadResponse == null || !uploadResponse.Success)
        {
            throw new Exception($"Schema upload failed: {uploadResponse?.ErrorMessage ?? "Unknown error"}");
        }

        _logger.log.Information(
            "Schema upload completed successfully. Processed {Count} schemas with reload ID {ReloadId}",
            uploadResponse.SchemasProcessed,
            uploadResponse.ReloadId
        );

        // Small delay to ensure schema is fully loaded
        await Task.Delay(500);
    }

    [Given(@"the schema is deployed")]
    public async Task GivenTheSchemaIsDeployed()
    {
        await WhenTheSchemaIsDeployedToTheDMS();
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Content;

public class ContentProviderTests
{
    private ILogger<ContentProvider>? _logger;
    private IOptions<AppSettings> _appSettings;
    private Assembly _assembly;
    private IAssemblyLoader _iAssemblyLoader;
    [SetUp]
    public void Setup()
    {
        _assembly = A.Fake<Assembly>();
        _iAssemblyLoader = A.Fake<IAssemblyLoader>();

        var resources = new string[] {
            "EdFi.DataStandard52.ApiSchema.ApiSchema.json",
            "EdFi.DataStandard52.ApiSchema.xsd.Interchange-AssessmentMetadata.xsd",
            "EdFi.DataStandard52.ApiSchema.xsd.Interchange-Descriptors.xsd" };

        A.CallTo(() => _assembly.GetManifestResourceNames()).Returns(resources);
        A.CallTo(() => _iAssemblyLoader.Load(A<string>._)).Returns(_assembly);

        _logger = A.Fake<ILogger<ContentProvider>>();

        _appSettings = Options.Create(new AppSettings
        {
            ApiSchemaPath = "some/valid/path",
            AllowIdentityUpdateOverrides = ""
        });
    }

    [Test]
    public void Returns_Expected_Json_Files()
    {
        // Arrange
        var contentProvider = new ContentProvider(_logger!, _appSettings, _iAssemblyLoader);

        // Act
        var response = contentProvider.Files("ApiSchema", ".json", "ed-fi");

        // Assert
        response.Should().NotBeNull();
        response.Count().Should().Be(1);
        response.First().Should().Be("EdFi.DataStandard52.ApiSchema.ApiSchema.json");
    }

    [Test]
    public void Returns_Expected_Xsd_Files()
    {
        // Arrange
        var contentProvider = new ContentProvider(_logger!, _appSettings, _iAssemblyLoader);

        // Act
        var response = contentProvider.Files("Interchange", ".xsd", "ed-fi");

        // Assert
        response.Should().NotBeNull();
        response.Count().Should().Be(2);
        response.Should().Contain("EdFi.DataStandard52.ApiSchema.xsd.Interchange-AssessmentMetadata.xsd");
    }

    [Test]
    public void Returns_Expected_Json_File_Content()
    {
        // Arrange
        var expectedHost = "http://local:5000";
        var expectedOauthUrl = "http://local:5000/oauth/token";

        var content =
            """{"openapi":"3.0.1", "info":"descriptors","servers":[{"url":"http://local:5000/data/v3"}],"oauth":[{"url":"http://local:5000/oauth/token"}]}""";
        var mockJsonNode = JsonNode.Parse(content)!;

        var contentProvider = A.Fake<IContentProvider>();
        A.CallTo(() => contentProvider.LoadJsonContent(A<string>._, A<string>._, A<string>._)).Returns(mockJsonNode);
        // Act
        var response = contentProvider.LoadJsonContent("EdFi.DataStandard52.ApiSchema.ApiSchema.json", expectedHost, expectedOauthUrl);
        var openApi = response?["openapi"]?.GetValue<string>();
        var serverUrl = response?["servers"]?.AsArray()?[0]?["url"]?.GetValue<string>();
        var oauthUrl = response?["oauth"]?.AsArray()?[0]?["url"]?.GetValue<string>();

        // Assert
        response.Should().NotBeNull();
        openApi.Should().Be("3.0.1");
        serverUrl.Should().Be($"{expectedHost}/data/v3");
        oauthUrl.Should().Be(expectedOauthUrl);
    }

    [Test]
    public void Returns_Error_With_Not_Existing_File()
    {
        // Arrange
        var contentProvider = new ContentProvider(_logger!, _appSettings, _iAssemblyLoader);

        // Act
        Action action = () => contentProvider.LoadJsonContent("not-exists", string.Empty, string.Empty);

        // Assert
        action.Should().Throw<InvalidOperationException>().WithMessage("Couldn't load find the resource");
    }

    [Test]
    public void Returns_Error_With_Null_Stream()
    {
        // Arrange
        A.CallTo(() => _assembly!.GetManifestResourceStream(A<string>.Ignored)).Returns(null);
        var contentProvider = new ContentProvider(_logger!, _appSettings, _iAssemblyLoader);

        // Act
        Action action = () => contentProvider.LoadJsonContent("file1", string.Empty, string.Empty);

        // Assert
        action.Should().Throw<InvalidOperationException>().WithMessage("Couldn't load find the resource");
    }

    [Test]
    public void Returns_Expected_Xsd_File_Content()
    {
        // Arrange
        var content = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">\n  <xs:include schemaLocation=\"Ed-Fi-Core.xsd\" />\n";
        MemoryStream contentStream = new(Encoding.UTF8.GetBytes(content.ToString()));

        var contentProvider = A.Fake<IContentProvider>();
        A.CallTo(() => contentProvider.LoadXsdContent(A<string>._))
         .Returns(new Lazy<Stream>(() => contentStream));

        // Act
        var response = contentProvider.LoadXsdContent("EdFi.DataStandard52.ApiSchema.xsd.Interchange-Contact.xsd");
        var responseStream = response.Value;
        string line = string.Empty;
        using (var reader = new StreamReader(responseStream))
        {
            line = reader.ReadToEnd();
        }

        // Assert
        line.Should().NotBeNullOrWhiteSpace();
        line.Should().Be(content);
    }
}

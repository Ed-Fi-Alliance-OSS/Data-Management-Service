// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Content;

public class ContentProviderTests
{
    private IAssemblyProvider? _assemblyProvider;
    private ILogger<ContentProvider>? _logger;
    private Assembly? _assembly;

    [SetUp]
    public void Setup()
    {
        _assemblyProvider = A.Fake<IAssemblyProvider>();

        _assembly = A.Fake<Assembly>();

        var resources = new string[] { "file1.json", "file2.xsd", "file3.xsd" };

        A.CallTo(() => _assembly.GetManifestResourceNames()).Returns(resources);

        A.CallTo(() => _assemblyProvider.GetAssemblyByType(A<Type>.Ignored)).Returns(_assembly);
        _logger = A.Fake<ILogger<ContentProvider>>();
    }

    [Test]
    public void Returns_Expected_Json_Files()
    {
        // Arrange
        var contentProvider = new ContentProvider(_logger!, _assemblyProvider!);

        // Act
        var response = contentProvider.Files("file", ".json");

        // Assert
        response.Should().NotBeNull();
        response.Count().Should().Be(1);
        response.First().Should().Be("file1.json");
    }

    [Test]
    public void Returns_Expected_Xsd_Files()
    {
        // Arrange
        var contentProvider = new ContentProvider(_logger!, _assemblyProvider!);

        // Act
        var response = contentProvider.Files("file", ".xsd");

        // Assert
        response.Should().NotBeNull();
        response.Count().Should().Be(2);
        response.Should().Contain("file2.xsd");
    }

    [Test]
    public void Returns_Expected_Json_File_Content()
    {
        // Arrange
        var expectedHost = "http://local:5000";
        var expectedOauthUrl = "http://local:5000/Oauth";
        var content =
            """{"openapi":"3.0.1", "info":"descriptors","servers":[{"url":"HOST_URL/data/v3"}],"oauth":[{"url":"HOST_URL/oauth/token"}]}""";
        MemoryStream contentStream = new(Encoding.UTF8.GetBytes(content.ToString()));

        A.CallTo(() => _assembly!.GetManifestResourceStream(A<string>.Ignored)).Returns(contentStream);
        var contentProvider = new ContentProvider(_logger!, _assemblyProvider!);

        // Act
        var response = contentProvider.LoadJsonContent("file", expectedHost, expectedOauthUrl);
        var openApi = response?["openapi"]?.GetValue<string>();
        var serverUrl = response?["servers"]?.AsArray()?[0]?["url"]?.GetValue<string>();
        var oauthUrl = response?["oauth"]?.AsArray()?[0]?["url"]?.GetValue<string>();

        // Assert
        response.Should().NotBeNull();
        openApi.Should().Be("3.0.1");
        serverUrl.Should().Be($"{expectedHost}/data");
        oauthUrl.Should().Be(expectedOauthUrl);
    }

    [Test]
    public void Returns_Error_With_Not_Existing_File()
    {
        // Arrange
        var contentProvider = new ContentProvider(_logger!, _assemblyProvider!);

        // Act
        Action action = () => contentProvider.LoadJsonContent("not-exists", string.Empty, string.Empty);

        // Assert
        action.Should().Throw<InvalidOperationException>().WithMessage("not-exists not found");
    }

    [Test]
    public void Returns_Error_With_Null_Stream()
    {
        // Arrange
        A.CallTo(() => _assembly!.GetManifestResourceStream(A<string>.Ignored)).Returns(null);
        var contentProvider = new ContentProvider(_logger!, _assemblyProvider!);

        // Act
        Action action = () => contentProvider.LoadJsonContent("file1", string.Empty, string.Empty);

        // Assert
        action.Should().Throw<InvalidOperationException>().WithMessage("Couldn't load file1.json");
    }

    [Test]
    public void Returns_Expected_Xsd_File_Content()
    {
        // Arrange
        var content = "xsd content";
        MemoryStream contentStream = new(Encoding.UTF8.GetBytes(content.ToString()));

        A.CallTo(() => _assembly!.GetManifestResourceStream(A<string>.Ignored)).Returns(contentStream);
        var contentProvider = new ContentProvider(_logger!, _assemblyProvider!);

        // Act
        var response = contentProvider.LoadXsdContent("file2");
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

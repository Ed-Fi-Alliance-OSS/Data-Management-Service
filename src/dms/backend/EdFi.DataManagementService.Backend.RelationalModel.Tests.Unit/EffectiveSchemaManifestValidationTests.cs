// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for emitting a manifest with a null effective schema info.
/// </summary>
[TestFixture]
public class Given_A_Null_EffectiveSchemaInfo
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        try
        {
            EffectiveSchemaManifestEmitter.Emit(null!);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should throw an ArgumentNullException.
    /// </summary>
    [Test]
    public void It_should_throw_ArgumentNullException()
    {
        _exception.Should().BeOfType<ArgumentNullException>();
    }
}

/// <summary>
/// Test fixture for emitting a manifest with an empty API schema format version.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Empty_ApiSchemaFormatVersion
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var info = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "edf1edf1",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: [0x01],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo(
                    "ed-fi",
                    "Ed-Fi",
                    "5.0.0",
                    false,
                    "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                ),
            ],
            ResourceKeysInIdOrder: []
        );

        try
        {
            EffectiveSchemaManifestEmitter.Emit(info);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should throw an ArgumentException.
    /// </summary>
    [Test]
    public void It_should_throw_ArgumentException()
    {
        _exception.Should().BeOfType<ArgumentException>();
        _exception!.Message.Should().Contain("ApiSchemaFormatVersion");
    }
}

/// <summary>
/// Test fixture for emitting a manifest with an empty relational mapping version.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Empty_RelationalMappingVersion
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var info = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "",
            EffectiveSchemaHash: "edf1edf1",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: [0x01],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo(
                    "ed-fi",
                    "Ed-Fi",
                    "5.0.0",
                    false,
                    "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                ),
            ],
            ResourceKeysInIdOrder: []
        );

        try
        {
            EffectiveSchemaManifestEmitter.Emit(info);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should throw an ArgumentException.
    /// </summary>
    [Test]
    public void It_should_throw_ArgumentException()
    {
        _exception.Should().BeOfType<ArgumentException>();
        _exception!.Message.Should().Contain("RelationalMappingVersion");
    }
}

/// <summary>
/// Test fixture for emitting a manifest with an empty resource key seed hash.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Empty_ResourceKeySeedHash
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var info = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "edf1edf1",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: [],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo(
                    "ed-fi",
                    "Ed-Fi",
                    "5.0.0",
                    false,
                    "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                ),
            ],
            ResourceKeysInIdOrder: []
        );

        try
        {
            EffectiveSchemaManifestEmitter.Emit(info);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should throw an ArgumentException.
    /// </summary>
    [Test]
    public void It_should_throw_ArgumentException()
    {
        _exception.Should().BeOfType<ArgumentException>();
        _exception!.Message.Should().Contain("ResourceKeySeedHash");
    }
}

/// <summary>
/// Test fixture for emitting a manifest with an empty effective schema hash.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Empty_EffectiveSchemaHash
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var info = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: [0x01],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo(
                    "ed-fi",
                    "Ed-Fi",
                    "5.0.0",
                    false,
                    "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                ),
            ],
            ResourceKeysInIdOrder: []
        );

        try
        {
            EffectiveSchemaManifestEmitter.Emit(info);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should throw an ArgumentException.
    /// </summary>
    [Test]
    public void It_should_throw_ArgumentException()
    {
        _exception.Should().BeOfType<ArgumentException>();
        _exception!.Message.Should().Contain("EffectiveSchemaHash");
    }
}

/// <summary>
/// Test fixture for emitting a manifest with mismatched resource key count.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Mismatched_ResourceKeyCount
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var info = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "edf1edf1",
            ResourceKeyCount: 5,
            ResourceKeySeedHash: [0x01],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo(
                    "ed-fi",
                    "Ed-Fi",
                    "5.0.0",
                    false,
                    "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                ),
            ],
            ResourceKeysInIdOrder:
            [
                new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "5.0.0", false),
            ]
        );

        try
        {
            EffectiveSchemaManifestEmitter.Emit(info);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should throw an ArgumentException.
    /// </summary>
    [Test]
    public void It_should_throw_ArgumentException()
    {
        _exception.Should().BeOfType<ArgumentException>();
        _exception!.Message.Should().Contain("ResourceKeyCount");
    }
}

/// <summary>
/// Test fixture for emitting a manifest with an empty project hash.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Empty_ProjectHash
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var info = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "edf1edf1",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: [0x01],
            SchemaComponentsInEndpointOrder: [new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false, "")],
            ResourceKeysInIdOrder: []
        );

        try
        {
            EffectiveSchemaManifestEmitter.Emit(info);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should throw an ArgumentException.
    /// </summary>
    [Test]
    public void It_should_throw_ArgumentException()
    {
        _exception.Should().BeOfType<ArgumentException>();
        _exception!.Message.Should().Contain("ProjectHash");
    }
}

/// <summary>
/// Test fixture for emitting a manifest with empty schema components.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Empty_SchemaComponents
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var info = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "edf1edf1",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: [0x01],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: []
        );

        try
        {
            EffectiveSchemaManifestEmitter.Emit(info);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should throw an ArgumentException.
    /// </summary>
    [Test]
    public void It_should_throw_ArgumentException()
    {
        _exception.Should().BeOfType<ArgumentException>();
        _exception!.Message.Should().Contain("SchemaComponentsInEndpointOrder");
    }
}

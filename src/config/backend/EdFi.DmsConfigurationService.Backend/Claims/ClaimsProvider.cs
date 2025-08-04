// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: InternalsVisibleTo("EdFi.DmsConfigurationService.Backend.Tests.Unit")]

namespace EdFi.DmsConfigurationService.Backend.Claims;

/// <summary>
/// Configuration options for claims loading
/// </summary>
public class ClaimsOptions
{
    public bool UseClaimsPath { get; set; }
    public string? ClaimsPath { get; set; }

    /// <summary>
    /// When UseClaimsPath is true, determines whether to load the base Claims.json from embedded resources
    /// instead of from the file system. This enables hybrid mode where the base claims come from embedded
    /// resources and extensions come from fragment files in the ClaimsPath directory.
    /// Only used when UseClaimsPath is true.
    /// </summary>
    public bool UseEmbeddedBaseClaims { get; set; } = false;

    /// <summary>
    /// Validates the configuration options for consistency
    /// </summary>
    public void Validate()
    {
        if (UseClaimsPath && string.IsNullOrWhiteSpace(ClaimsPath))
        {
            throw new InvalidOperationException("ClaimsPath must be set when UseClaimsPath is true");
        }

        if (!UseClaimsPath && UseEmbeddedBaseClaims)
        {
            throw new InvalidOperationException(
                "UseEmbeddedBaseClaims is only valid when UseClaimsPath is true"
            );
        }
    }
}

/// <summary>
/// Validator for ClaimsOptions configuration
/// </summary>
public class ClaimsOptionsValidator : IValidateOptions<ClaimsOptions>
{
    public ValidateOptionsResult Validate(string? name, ClaimsOptions options)
    {
        if (options.UseClaimsPath && string.IsNullOrWhiteSpace(options.ClaimsPath))
        {
            return ValidateOptionsResult.Fail("ClaimsPath must be set when UseClaimsPath is true");
        }

        if (!options.UseClaimsPath && options.UseEmbeddedBaseClaims)
        {
            return ValidateOptionsResult.Fail(
                "UseEmbeddedBaseClaims is only valid when UseClaimsPath is true"
            );
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Loads and manages claims from files and uploads
/// </summary>
public class ClaimsProvider : IClaimsProvider
{
    private readonly ILogger<ClaimsProvider> _logger;
    private readonly IOptions<ClaimsOptions> _claimsOptions;
    private readonly IClaimsValidator _claimsValidator;
    private readonly IClaimsFragmentComposer _claimsFragmentComposer;

    // Cached claims nodes
    private ClaimsDocumentNodes? _claimsNodes;

    // Unique identifier for the current reload instance
    private Guid _reloadId = Guid.NewGuid();

    // Lock object to ensure thread-safe access during claims reloads
    private readonly object _reloadLock = new();

    // Validation state
    private bool _isClaimsValid = true;
    private List<ClaimsFailure> _claimsFailures = [];

    public ClaimsProvider(
        ILogger<ClaimsProvider> logger,
        IOptions<ClaimsOptions> claimsOptions,
        IClaimsValidator claimsValidator,
        IClaimsFragmentComposer claimsFragmentComposer
    )
    {
        _logger = logger;
        _claimsOptions = claimsOptions;
        _claimsValidator = claimsValidator;
        _claimsFragmentComposer = claimsFragmentComposer;

        // Validate configuration at startup
        _claimsOptions.Value.Validate();
    }

    /// <summary>
    /// Gets the current reload identifier
    /// </summary>
    public Guid ReloadId
    {
        get
        {
            lock (_reloadLock)
            {
                return _reloadId;
            }
        }
    }

    /// <summary>
    /// Gets whether the currently loaded claims are valid
    /// </summary>
    public bool IsClaimsValid
    {
        get
        {
            lock (_reloadLock)
            {
                return _isClaimsValid;
            }
        }
    }

    /// <summary>
    /// Gets the failures from the last claims operation
    /// </summary>
    public List<ClaimsFailure> ClaimsFailures
    {
        get
        {
            lock (_reloadLock)
            {
                return _claimsFailures;
            }
        }
    }

    /// <summary>
    /// Returns claims document nodes containing claim sets and claims hierarchy
    /// </summary>
    public ClaimsDocumentNodes GetClaimsDocumentNodes()
    {
        lock (_reloadLock)
        {
            if (_claimsNodes == null)
            {
                // Initial load
                var loadResult = LoadClaimsFromSource();

                if (loadResult.Failures.Count > 0)
                {
                    _claimsFailures = loadResult.Failures;
                    _isClaimsValid = false;

                    throw new InvalidOperationException(
                        "Claims loading failed. Check ClaimsFailures for details."
                    );
                }

                if (loadResult.Nodes == null)
                {
                    var failure = new ClaimsFailure(
                        "Configuration",
                        "Claims loading returned null without failures"
                    );
                    _claimsFailures = [failure];
                    _isClaimsValid = false;
                    throw new InvalidOperationException(failure.Message);
                }

                var validationResult = ValidateClaims(loadResult.Nodes);
                if (!validationResult.Success)
                {
                    _claimsFailures = validationResult.Failures;
                    _isClaimsValid = false;
                    throw new InvalidOperationException(
                        "Claims validation failed. Cannot proceed with invalid claims."
                    );
                }

                _claimsNodes = loadResult.Nodes;
                _logger.LogInformation("Initial claims load completed successfully.");
            }
            return _claimsNodes;
        }
    }

    /// <summary>
    /// Updates the in-memory claims state after successful database update
    /// </summary>
    public void UpdateInMemoryState(ClaimsDocumentNodes claimsNodes, Guid newReloadId)
    {
        lock (_reloadLock)
        {
            _claimsNodes = claimsNodes;
            _reloadId = newReloadId;
            _claimsFailures = [];
            _isClaimsValid = true;
            _logger.LogInformation("In-memory claims state updated with reload ID: {ReloadId}", _reloadId);
        }
    }

    /// <summary>
    /// Validates claims without updating state
    /// </summary>
    internal ClaimsLoadStatus ValidateClaimsWithoutUpdate(JsonNode claimsJson)
    {
        _logger.LogInformation("Validating claims from provided JSON...");

        try
        {
            // Create new claims nodes - let JSON Schema validation handle structure validation
            var claimSetsNode = claimsJson["claimSets"];
            var claimsHierarchyNode = claimsJson["claimsHierarchy"];

            if (claimSetsNode == null || claimsHierarchyNode == null)
            {
                // Perform validation to get proper error messages
                var validationFailures = _claimsValidator.Validate(claimsJson);
                var failures = validationFailures
                    .Select(vf => new ClaimsFailure(
                        "Validation",
                        string.Join(", ", vf.FailureMessages),
                        vf.FailurePath.Value
                    ))
                    .ToList();

                if (failures.Count == 0)
                {
                    // Shouldn't happen, but add a generic failure if validation didn't catch it
                    failures.Add(new ClaimsFailure("Configuration", "Claims JSON structure is invalid"));
                }

                return new ClaimsLoadStatus(false, failures);
            }

            var newClaimsNodes = new ClaimsDocumentNodes(claimSetsNode, claimsHierarchyNode);
            var validationResult = ValidateClaims(newClaimsNodes);
            return validationResult;
        }
        catch (Exception ex)
        {
            var failure = new ClaimsFailure("ParseError", "Failed to process uploaded claims", null, ex);
            _logger.LogError(ex, failure.Message);
            return new ClaimsLoadStatus(false, [failure]);
        }
    }

    /// <summary>
    /// Loads claims from the configured source
    /// </summary>
    public virtual ClaimsLoadResult LoadClaimsFromSource()
    {
        _logger.LogInformation("Loading claims from configured source...");

        if (!_claimsOptions.Value.UseClaimsPath)
        {
            // Pure Embedded Mode (E2E Testing)
            _logger.LogInformation("Using Pure Embedded Mode - loading from assembly resource");
            return LoadClaimsFromAssembly();
        }
        else if (_claimsOptions.Value.UseEmbeddedBaseClaims)
        {
            // Hybrid Mode (Production with Embedded Base)
            _logger.LogInformation(
                "Using Hybrid Mode - loading base from embedded resource with fragments from file system"
            );
            return LoadClaimsFromFileSystemWithFragments();
        }
        else
        {
            // Pure File System Mode (Production)
            _logger.LogInformation(
                "Using Pure File System Mode - loading base and fragments from file system"
            );
            return LoadClaimsFromFileSystemWithFragments();
        }
    }

    /// <summary>
    /// Loads claims from the file system with fragment composition support
    /// </summary>
    private ClaimsLoadResult LoadClaimsFromFileSystemWithFragments()
    {
        if (_claimsOptions.Value.ClaimsPath == null)
        {
            var failure = new ClaimsFailure("Configuration", "No ClaimsPath configuration is set");
            _logger.LogError(failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }

        string claimsPath = _claimsOptions.Value.ClaimsPath;

        if (!Directory.Exists(claimsPath))
        {
            var failure = new ClaimsFailure("FileSystem", $"The directory {claimsPath} does not exist");
            _logger.LogError(failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }

        try
        {
            ClaimsLoadResult baseClaimsResult;

            if (_claimsOptions.Value.UseEmbeddedBaseClaims)
            {
                // Hybrid Mode: Load base from embedded resource
                _logger.LogInformation("Loading base Claims.json from embedded resource");
                baseClaimsResult = LoadClaimsFromAssembly();
            }
            else
            {
                // Pure File System Mode: Load base from file system
                _logger.LogInformation("Loading base Claims.json from file system");
                baseClaimsResult = LoadBaseClaimsFromFileSystem(claimsPath);
            }

            if (baseClaimsResult.Failures.Count > 0)
            {
                return baseClaimsResult;
            }

            // Apply fragment composition if base claims loaded successfully
            if (baseClaimsResult.Nodes != null)
            {
                _logger.LogInformation("Applying fragment composition to base claims");
                var compositionResult = _claimsFragmentComposer.ComposeClaimsFromFragments(
                    baseClaimsResult.Nodes,
                    claimsPath
                );

                if (compositionResult.Failures.Count > 0)
                {
                    _logger.LogWarning("Fragment composition had failures - returning base claims only");
                    // Log failures but continue with base claims
                    foreach (var failure in compositionResult.Failures)
                    {
                        _logger.LogWarning("Fragment composition failure: {Message}", failure.Message);
                    }
                    return baseClaimsResult;
                }

                if (compositionResult.Nodes != null)
                {
                    _logger.LogInformation("Fragment composition completed successfully");
                    return compositionResult;
                }
            }

            return baseClaimsResult;
        }
        catch (Exception ex)
        {
            var failure = new ClaimsFailure("FileSystem", "Failed to load claims with fragments", null, ex);
            _logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
    }

    /// <summary>
    /// Loads base claims from the file system (legacy single-file approach)
    /// </summary>
    private ClaimsLoadResult LoadBaseClaimsFromFileSystem(string claimsPath)
    {
        var claimsFiles = Directory.GetFiles(claimsPath, "Claims.json", SearchOption.AllDirectories);

        if (claimsFiles.Length == 0)
        {
            var failure = new ClaimsFailure(
                "FileSystem",
                $"No Claims.json file found in directory {claimsPath}"
            );
            _logger.LogError(failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }

        if (claimsFiles.Length > 1)
        {
            var failure = new ClaimsFailure(
                "Configuration",
                $"Multiple Claims.json files found in directory {claimsPath}"
            );
            _logger.LogError(failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }

        try
        {
            _logger.LogInformation("Loading Claims.json file: {FilePath}", claimsFiles[0]);
            string fileContent = File.ReadAllText(claimsFiles[0]);

            JsonNode? claimsJson = JsonNode.Parse(fileContent);
            if (claimsJson == null)
            {
                var failure = new ClaimsFailure(
                    "ParseError",
                    $"Unable to parse Claims.json file at '{claimsFiles[0]}' - parsed to null"
                );
                _logger.LogError(failure.Message);
                return new ClaimsLoadResult(null, [failure]);
            }

            var claimSetsNode = claimsJson["claimSets"];
            var claimsHierarchyNode = claimsJson["claimsHierarchy"];

            if (claimSetsNode == null || claimsHierarchyNode == null)
            {
                var failure = new ClaimsFailure(
                    "Configuration",
                    "Claims.json missing required properties (claimSets or claimsHierarchy)"
                );
                _logger.LogError(failure.Message);
                return new ClaimsLoadResult(null, [failure]);
            }

            return new ClaimsLoadResult(new ClaimsDocumentNodes(claimSetsNode, claimsHierarchyNode), []);
        }
        catch (Exception ex)
        {
            var failure = new ClaimsFailure("FileSystem", "Failed to load Claims.json", null, ex);
            _logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
    }

    /// <summary>
    /// Gets the assembly to use for embedded resource loading
    /// </summary>
    protected virtual Assembly GetAssemblyForEmbeddedResource()
    {
        return Assembly.GetExecutingAssembly();
    }

    /// <summary>
    /// Loads claims from assembly
    /// </summary>
    private ClaimsLoadResult LoadClaimsFromAssembly()
    {
        try
        {
            var assembly = GetAssemblyForEmbeddedResource();
            string resourceName = $"{assembly.GetName().Name}.Deploy.Claims.json";

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                var failure = new ClaimsFailure(
                    "Configuration",
                    $"Could not load assembly-bundled Claims.json file '{resourceName}'"
                );
                _logger.LogError(failure.Message);
                return new ClaimsLoadResult(null, [failure]);
            }

            using StreamReader reader = new(stream);
            string jsonContent = reader.ReadToEnd();

            JsonNode? claimsJson = JsonNode.Parse(jsonContent);
            if (claimsJson == null)
            {
                var failure = new ClaimsFailure(
                    "ParseError",
                    "Unable to parse assembly-bundled Claims.json file"
                );
                _logger.LogError(failure.Message);
                return new ClaimsLoadResult(null, [failure]);
            }

            var claimSetsNode = claimsJson["claimSets"];
            var claimsHierarchyNode = claimsJson["claimsHierarchy"];

            if (claimSetsNode == null || claimsHierarchyNode == null)
            {
                var failure = new ClaimsFailure(
                    "Configuration",
                    "Assembly Claims.json missing required properties"
                );
                _logger.LogError(failure.Message);
                return new ClaimsLoadResult(null, [failure]);
            }

            _logger.LogInformation("Loaded Claims.json from assembly resource");

            return new ClaimsLoadResult(new ClaimsDocumentNodes(claimSetsNode, claimsHierarchyNode), []);
        }
        catch (Exception ex)
        {
            var failure = new ClaimsFailure(
                "FileSystem",
                "Failed to load Claims.json from assembly",
                null,
                ex
            );
            _logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
    }

    /// <summary>
    /// Validates the claims structure using JSON Schema
    /// </summary>
    private ClaimsLoadStatus ValidateClaims(ClaimsDocumentNodes claimsNodes)
    {
        List<ClaimsFailure> failures = [];

        try
        {
            // Create a combined JSON node for validation
            // We need to serialize and deserialize to avoid "node already has a parent" error
            var claimSetsJson = claimsNodes.ClaimSetsNode.ToJsonString();
            var hierarchyJson = claimsNodes.ClaimsHierarchyNode.ToJsonString();

            // Debug logging to identify null values
            _logger.LogInformation("ClaimSets JSON length: {Length}", claimSetsJson.Length);
            _logger.LogInformation("Hierarchy JSON length: {Length}", hierarchyJson.Length);

            // Save the full combined JSON to a file for detailed analysis
            var combinedForAnalysis = new JsonObject
            {
                ["claimSets"] = JsonNode.Parse(claimSetsJson),
                ["claimsHierarchy"] = JsonNode.Parse(hierarchyJson),
            };
            _logger.LogInformation(
                "Full combined JSON for validation: {CombinedJson}",
                combinedForAnalysis.ToJsonString()
            );

            var claimSetsNode = JsonNode.Parse(claimSetsJson);
            var hierarchyNode = JsonNode.Parse(hierarchyJson);

            // Check for null parsing results
            if (claimSetsNode == null)
            {
                failures.Add(new ClaimsFailure("Validation", "ClaimSets node parsed to null", null));
                return new ClaimsLoadStatus(false, failures);
            }

            if (hierarchyNode == null)
            {
                failures.Add(new ClaimsFailure("Validation", "ClaimsHierarchy node parsed to null", null));
                return new ClaimsLoadStatus(false, failures);
            }

            var combinedClaims = new JsonObject
            {
                ["claimSets"] = claimSetsNode,
                ["claimsHierarchy"] = hierarchyNode,
            };

            // Validate using JSON Schema
            List<ClaimsValidationFailure> validationFailures = _claimsValidator.Validate(combinedClaims);

            if (validationFailures.Count > 0)
            {
                _logger.LogError(
                    "Claims validation failed with {FailureCount} failures",
                    validationFailures.Count
                );

                foreach (var error in validationFailures)
                {
                    _logger.LogError(
                        "Validation error at path '{FailurePath}': {FailureMessages}",
                        error.FailurePath.Value,
                        string.Join(", ", error.FailureMessages)
                    );

                    failures.Add(
                        new ClaimsFailure(
                            "Validation",
                            string.Join(", ", error.FailureMessages),
                            error.FailurePath.Value
                        )
                    );
                }
            }
            else
            {
                _logger.LogInformation("Claims validation passed successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate claims");
            failures.Add(new ClaimsFailure("Validation", "Failed to validate claims structure", null, ex));
        }

        return new ClaimsLoadStatus(failures.Count == 0, failures);
    }
}

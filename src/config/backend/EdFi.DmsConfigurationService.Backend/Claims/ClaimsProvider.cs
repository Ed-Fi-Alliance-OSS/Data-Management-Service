// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: InternalsVisibleTo("EdFi.DmsConfigurationService.Backend.Tests.Unit")]

namespace EdFi.DmsConfigurationService.Backend.Claims;

/// <summary>
/// Defines the source for loading claims configuration
/// </summary>
public enum ClaimsSource
{
    /// <summary>
    /// Load claims from embedded resources only (E2E Testing)
    /// </summary>
    Embedded,

    /// <summary>
    /// Load base claims from embedded resources with fragments from filesystem (Production with Embedded Base)
    /// </summary>
    Hybrid,

    /// <summary>
    /// Load all claims from filesystem (Production)
    /// </summary>
    Filesystem,
}

/// <summary>
/// Configuration options for claims loading
/// </summary>
public class ClaimsOptions
{
    /// <summary>
    /// The source for loading claims configuration
    /// </summary>
    public ClaimsSource ClaimsSource { get; set; } = ClaimsSource.Embedded;

    /// <summary>
    /// Directory path for filesystem-based claims loading
    /// </summary>
    public string ClaimsDirectory { get; set; } = "";

    /// <summary>
    /// When enabled, allows dynamic loading and reloading of claims via management endpoints.
    /// WARNING: This feature allows runtime modification of security claims and should only be
    /// enabled in development or testing environments. Default is false.
    /// </summary>
    public bool DangerouslyEnableUnrestrictedClaimsLoading { get; set; } = false;

    /// <summary>
    /// Validates the configuration options for consistency
    /// </summary>
    public void Validate()
    {
        if (
            (ClaimsSource == ClaimsSource.Hybrid || ClaimsSource == ClaimsSource.Filesystem)
            && string.IsNullOrWhiteSpace(ClaimsDirectory)
        )
        {
            throw new InvalidOperationException(
                "ClaimsDirectory must be set when ClaimsSource is Hybrid or Filesystem"
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
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Loads and manages claims from files and uploads
/// </summary>
public class ClaimsProvider(
    ILogger<ClaimsProvider> logger,
    IOptions<ClaimsOptions> claimsOptions,
    IClaimsValidator claimsValidator,
    IClaimsFragmentComposer claimsFragmentComposer
) : IClaimsProvider
{
    // Cached claims nodes
    private ClaimsDocument? _claimsNodes;

    // Unique identifier for the current reload instance
    private Guid _reloadId = Guid.NewGuid();

    // Lock object to ensure thread-safe access during claims reloads
    private readonly object _reloadLock = new();

    // Validation state
    private bool _isClaimsValid = true;
    private List<ClaimsFailure> _claimsFailures = [];

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
    public ClaimsDocument GetClaimsDocumentNodes()
    {
        lock (_reloadLock)
        {
            if (_claimsNodes == null)
            {
                // Initial load
                ClaimsLoadResult loadResult = LoadClaimsFromSource();

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
                    ClaimsFailure failure = new(
                        "Configuration",
                        "Claims loading returned null without failures"
                    );
                    _claimsFailures = [failure];
                    _isClaimsValid = false;
                    throw new InvalidOperationException(failure.Message);
                }

                ClaimsLoadStatus validationResult = ValidateClaims(loadResult.Nodes);
                if (!validationResult.Success)
                {
                    _claimsFailures = validationResult.Failures;
                    _isClaimsValid = false;
                    throw new InvalidOperationException(
                        "Claims validation failed. Cannot proceed with invalid claims."
                    );
                }

                _claimsNodes = loadResult.Nodes;
                logger.LogInformation("Initial claims load completed successfully.");
            }
            return _claimsNodes;
        }
    }

    /// <summary>
    /// Updates the in-memory claims state after successful database update
    /// </summary>
    public void UpdateInMemoryState(ClaimsDocument claimsNodes, Guid newReloadId)
    {
        lock (_reloadLock)
        {
            _claimsNodes = claimsNodes;
            _reloadId = newReloadId;
            _claimsFailures = [];
            _isClaimsValid = true;
            logger.LogInformation("In-memory claims state updated with reload ID: {ReloadId}", _reloadId);
        }
    }

    /// <summary>
    /// Loads claims from the configured source
    /// </summary>
    public virtual ClaimsLoadResult LoadClaimsFromSource()
    {
        logger.LogInformation(
            "Loading claims from configured source: {ClaimsSource}",
            claimsOptions.Value.ClaimsSource
        );

        switch (claimsOptions.Value.ClaimsSource)
        {
            case ClaimsSource.Embedded:
                logger.LogInformation("Using Embedded Mode - loading claims from assembly resource");
                return LoadClaimsFromAssembly();

            case ClaimsSource.Hybrid:
                logger.LogInformation(
                    "Using Hybrid Mode - loading base claims from embedded resource with fragments from file system"
                );
                return LoadClaimsWithFragments();

            case ClaimsSource.Filesystem:
                logger.LogInformation(
                    "Using Filesystem Mode - loading base claims and fragments from file system"
                );
                return LoadClaimsWithFragments();

            default:
                throw new InvalidOperationException(
                    $"Unsupported ClaimsSource: {claimsOptions.Value.ClaimsSource}"
                );
        }
    }

    /// <summary>
    /// Loads claims (either embedded or file system) with fragment composition support
    /// </summary>
    private ClaimsLoadResult LoadClaimsWithFragments()
    {
        if (string.IsNullOrWhiteSpace(claimsOptions.Value.ClaimsDirectory))
        {
            ClaimsFailure failure = new("Configuration", "No ClaimsDirectory configuration is set");
            logger.LogError(failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }

        string claimsDirectory = claimsOptions.Value.ClaimsDirectory;

        if (!Directory.Exists(claimsDirectory))
        {
            ClaimsFailure failure = new("FileSystem", $"The directory {claimsDirectory} does not exist");
            logger.LogError(failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }

        try
        {
            ClaimsLoadResult baseClaimsResult;

            if (claimsOptions.Value.ClaimsSource == ClaimsSource.Hybrid)
            {
                // Hybrid Mode: Load base from embedded resource
                logger.LogInformation("Loading base Claims.json from embedded resource");
                baseClaimsResult = LoadClaimsFromAssembly();
            }
            else
            {
                // Filesystem Mode: Load base from file system
                logger.LogInformation("Loading base Claims.json from file system");
                baseClaimsResult = LoadBaseClaimsFromFileSystem(claimsDirectory);
            }

            if (baseClaimsResult.Failures.Count > 0)
            {
                return baseClaimsResult;
            }

            // Apply fragment composition if base claims loaded successfully
            if (baseClaimsResult.Nodes != null)
            {
                logger.LogInformation("Applying fragment composition to base claims");
                ClaimsLoadResult compositionResult = claimsFragmentComposer.ComposeClaimsFromFragments(
                    baseClaimsResult.Nodes,
                    claimsDirectory
                );

                if (compositionResult.Failures.Count > 0)
                {
                    logger.LogWarning("Fragment composition had failures - returning base claims only");
                    // Log failures but continue with base claims
                    foreach (ClaimsFailure failure in compositionResult.Failures)
                    {
                        logger.LogWarning("Fragment composition failure: {Message}", failure.Message);
                    }
                    return baseClaimsResult;
                }

                if (compositionResult.Nodes != null)
                {
                    logger.LogInformation("Fragment composition completed successfully");
                    return compositionResult;
                }
            }

            return baseClaimsResult;
        }
        catch (DirectoryNotFoundException ex)
        {
            ClaimsFailure failure = new(
                "DirectoryNotFound",
                "Claims fragments directory not found",
                null,
                ex
            );
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
        catch (IOException ex)
        {
            ClaimsFailure failure = new("IOError", "Failed to read claims fragment files", null, ex);
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
        catch (InvalidOperationException ex)
        {
            ClaimsFailure failure = new(
                "OperationError",
                "Invalid operation during claims loading",
                null,
                ex
            );
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
    }

    /// <summary>
    /// Loads base claims from the file system (legacy single-file approach)
    /// </summary>
    private ClaimsLoadResult LoadBaseClaimsFromFileSystem(string claimsDirectory)
    {
        string[] claimsFiles = Directory.GetFiles(
            claimsDirectory,
            "Claims.json",
            SearchOption.AllDirectories
        );

        if (claimsFiles.Length == 0)
        {
            ClaimsFailure failure = new(
                "FileSystem",
                $"No Claims.json file found in directory {claimsDirectory}"
            );
            logger.LogError(failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }

        if (claimsFiles.Length > 1)
        {
            ClaimsFailure failure = new(
                "Configuration",
                $"Multiple Claims.json files found in directory {claimsDirectory}"
            );
            logger.LogError(failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }

        try
        {
            logger.LogInformation("Loading Claims.json file: {FilePath}", claimsFiles[0]);
            string fileContent = File.ReadAllText(claimsFiles[0]);

            JsonNode? claimsJson = JsonNode.Parse(fileContent);
            if (claimsJson == null)
            {
                ClaimsFailure failure = new(
                    "ParseError",
                    $"Unable to parse Claims.json file at '{claimsFiles[0]}' - parsed to null"
                );
                logger.LogError(failure.Message);
                return new ClaimsLoadResult(null, [failure]);
            }

            JsonNode? claimSetsNode = claimsJson["claimSets"];
            JsonNode? claimsHierarchyNode = claimsJson["claimsHierarchy"];

            if (claimSetsNode == null || claimsHierarchyNode == null)
            {
                ClaimsFailure failure = new(
                    "Configuration",
                    "Claims.json missing required properties (claimSets or claimsHierarchy)"
                );
                logger.LogError(failure.Message);
                return new ClaimsLoadResult(null, [failure]);
            }

            return new ClaimsLoadResult(new ClaimsDocument(claimSetsNode, claimsHierarchyNode), []);
        }
        catch (JsonException ex)
        {
            ClaimsFailure failure = new("JsonError", "Invalid JSON format in Claims.json", null, ex);
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
        catch (FileNotFoundException ex)
        {
            ClaimsFailure failure = new("FileNotFound", "Claims.json file not found", null, ex);
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
        catch (IOException ex)
        {
            ClaimsFailure failure = new("IOError", "Failed to read Claims.json file", null, ex);
            logger.LogError(ex, failure.Message);
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
            Assembly assembly = GetAssemblyForEmbeddedResource();
            string resourceName = $"{assembly.GetName().Name}.Claims.Claims.json";

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                ClaimsFailure failure = new(
                    "Configuration",
                    $"Could not load assembly-bundled Claims.json file '{resourceName}'"
                );
                logger.LogError(failure.Message);
                return new ClaimsLoadResult(null, [failure]);
            }

            using StreamReader reader = new(stream);
            string jsonContent = reader.ReadToEnd();

            JsonNode? claimsJson = JsonNode.Parse(jsonContent);
            if (claimsJson == null)
            {
                ClaimsFailure failure = new(
                    "ParseError",
                    "Unable to parse assembly-bundled Claims.json file"
                );
                logger.LogError(failure.Message);
                return new ClaimsLoadResult(null, [failure]);
            }

            JsonNode? claimSetsNode = claimsJson["claimSets"];
            JsonNode? claimsHierarchyNode = claimsJson["claimsHierarchy"];

            if (claimSetsNode == null || claimsHierarchyNode == null)
            {
                ClaimsFailure failure = new(
                    "Configuration",
                    "Assembly Claims.json missing required properties"
                );
                logger.LogError(failure.Message);
                return new ClaimsLoadResult(null, [failure]);
            }

            logger.LogInformation("Loaded Claims.json from assembly resource");

            return new ClaimsLoadResult(new ClaimsDocument(claimSetsNode, claimsHierarchyNode), []);
        }
        catch (JsonException ex)
        {
            ClaimsFailure failure = new("JsonError", "Invalid JSON format in embedded Claims.json", null, ex);
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
        catch (IOException ex)
        {
            ClaimsFailure failure = new(
                "IOError",
                "Failed to read embedded Claims.json from assembly",
                null,
                ex
            );
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
        catch (InvalidOperationException ex)
        {
            ClaimsFailure failure = new(
                "ResourceError",
                "Embedded Claims.json resource not found in assembly",
                null,
                ex
            );
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
    }

    /// <summary>
    /// Validates the claims structure using JSON Schema
    /// </summary>
    private ClaimsLoadStatus ValidateClaims(ClaimsDocument claimsNodes)
    {
        List<ClaimsFailure> failures = [];

        try
        {
            // Create a combined JSON node for validation
            // We need to serialize and deserialize to avoid "node already has a parent" error
            string claimSetsJson = claimsNodes.ClaimSetsNode.ToJsonString();
            string hierarchyJson = claimsNodes.ClaimsHierarchyNode.ToJsonString();

            // Parse JSON nodes for validation
            logger.LogDebug(
                "Parsing claims JSON for validation. ClaimSets length: {ClaimSetsLength}, Hierarchy length: {HierarchyLength}",
                claimSetsJson.Length,
                hierarchyJson.Length
            );

            JsonNode? claimSetsNode = JsonNode.Parse(claimSetsJson);
            JsonNode? hierarchyNode = JsonNode.Parse(hierarchyJson);

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

            JsonObject claimsDocument = new()
            {
                ["claimSets"] = claimSetsNode,
                ["claimsHierarchy"] = hierarchyNode,
            };

            // Validate using JSON Schema
            List<ClaimsValidationFailure> validationFailures = claimsValidator.Validate(claimsDocument);

            if (validationFailures.Count > 0)
            {
                logger.LogError(
                    "Claims validation failed with {FailureCount} failures",
                    validationFailures.Count
                );

                foreach (ClaimsValidationFailure error in validationFailures)
                {
                    logger.LogError(
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
                logger.LogInformation("Claims validation passed successfully");
            }
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Invalid arguments provided for claims validation");
            failures.Add(
                new ClaimsFailure("ArgumentError", "Invalid arguments for claims validation", null, ex)
            );
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid operation during claims validation");
            failures.Add(
                new ClaimsFailure("ValidationError", "Failed to validate claims structure", null, ex)
            );
        }

        return new ClaimsLoadStatus(failures.Count == 0, failures);
    }
}

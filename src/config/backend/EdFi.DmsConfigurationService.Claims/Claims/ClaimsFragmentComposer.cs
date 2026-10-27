// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Claims.Composition;

/// <summary>
/// Composes a complete claims document from base Claims.json and claim-set fragments,
/// applying resource grants to the hierarchy and registering fragment-defined claim sets.
/// </summary>
public class ClaimsFragmentComposer
{
    private readonly ILogger _logger;
    private readonly IClaimsCompositionFileSystem _fileSystem;

    public ClaimsFragmentComposer(ILogger logger)
        : this(logger, new ClaimsCompositionFileSystem()) { }

    internal ClaimsFragmentComposer(ILogger logger, IClaimsCompositionFileSystem fileSystem)
    {
        _logger = logger;
        _fileSystem = fileSystem;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Options for final output that uses camelCase like the base Claims.json
    private static readonly JsonSerializerOptions _outputJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // Convert to camelCase for output
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // Omit null values
    };

    /// <summary>
    /// Composes claims from base Claims.json and fragment files in the specified directory
    /// </summary>
    [SuppressMessage(
        "Major Code Smell",
        "S6667",
        Justification = "Raw exception text may contain input paths or JSON; log sanitized text and retain the original exception only in the structured result."
    )]
    public ClaimsCompositionResult ComposeClaimsFromFragments(
        ClaimsDocument baseClaimsNodes,
        string fragmentsPath
    )
    {
        List<ClaimsCompositionDiagnostic> diagnostics = [];
        string sourcePath = fragmentsPath;
        try
        {
            _logger.LogInformation(
                "Composing claims from base and fragments in path: {FragmentsPath}",
                LoggingUtility.SanitizeForLog(fragmentsPath)
            );

            // Discover fragment files
            List<string> fragmentFiles = DiscoverFragmentFiles(fragmentsPath, diagnostics);
            if (fragmentFiles.Count == 0)
            {
                _logger.LogInformation("No fragment files found - returning base claims");
                return new(new ClaimsLoadResult(baseClaimsNodes, []), diagnostics.ToArray());
            }

            _logger.LogInformation("Found {FragmentCount} fragment files to compose", fragmentFiles.Count);

            // Convert base claims to our transformation model
            List<TransformationClaim> baseClaims = ConvertToTransformationModel(
                baseClaimsNodes.ClaimsHierarchyNode
            );
            _logger.LogDebug(
                "Base claims converted to transformation model: {BaseClaimsCount} claims",
                baseClaims.Count
            );

            // Apply each fragment transformation, collecting the claim sets the fragments define
            List<ClaimSetDefinition> definedClaimSets = [];
            foreach (string fragmentFile in fragmentFiles)
            {
                _logger.LogInformation(
                    "Applying fragment: {FragmentFile}",
                    LoggingUtility.SanitizeForLog(fragmentFile)
                );
                sourcePath = fragmentFile;
                if (
                    ApplyFragmentTransformation(fragmentFile, baseClaims, diagnostics)
                    is ClaimSetDefinition definition
                )
                {
                    // An invalid name is not registered, but the composition continues: failing it
                    // would make Hybrid mode fall back to the base claims and drop every fragment
                    if (ClaimSetNameValidator.IsValid(definition.Name))
                    {
                        definedClaimSets.Add(definition);
                    }
                    else
                    {
                        diagnostics.Add(
                            new(
                                ClaimsCompositionDiagnosticCode.InvalidClaimSetRegistration,
                                fragmentFile,
                                ClaimSetName: definition.Name,
                                JsonPath: definition.JsonPath
                            )
                        );
                        _logger.LogError(
                            "Fragment {FragmentFile} defines a claim set name that is empty, longer than {MaxLength} characters, or contains white space; the claim set is not registered",
                            LoggingUtility.SanitizeForLog(fragmentFile),
                            ValidationConstants.ClaimSetNameMaxLength
                        );
                    }
                }
                _logger.LogDebug(
                    "After applying {FragmentFile}: {ClaimsCount} claims",
                    LoggingUtility.SanitizeForLog(fragmentFile),
                    baseClaims.Count
                );
            }

            // Clean up empty collections before conversion to ensure JSON Schema compliance
            CleanupEmptyCollections(baseClaims);

            // Convert back to ClaimsDocumentNodes format
            JsonNode transformedHierarchy = ConvertFromTransformationModel(baseClaims);

            // Debug logging to check the composed results
            _logger.LogInformation(
                "Transformed hierarchy JSON: {HierarchyJson}",
                LoggingUtility.SanitizeForLog(transformedHierarchy?.ToJsonString())
            );
            _logger.LogInformation(
                "Base claim sets JSON: {ClaimSetsJson}",
                LoggingUtility.SanitizeForLog(baseClaimsNodes.ClaimSetsNode?.ToJsonString())
            );

            // Ensure both nodes are not null
            if (transformedHierarchy is null)
            {
                transformedHierarchy = JsonNode.Parse("[]")!;
            }

            JsonNode claimSetsNode = WithDefinedClaimSets(
                baseClaimsNodes.ClaimSetsNode,
                definedClaimSets,
                diagnostics
            );
            ClaimsDocument composedNodes = new ClaimsDocument(claimSetsNode, transformedHierarchy);

            _logger.LogInformation(
                "Successfully composed claims from {FragmentCount} fragments",
                fragmentFiles.Count
            );
            return new(new ClaimsLoadResult(composedNodes, []), diagnostics.ToArray());
        }
        catch (JsonException ex)
        {
            ClaimsFailure failure = new ClaimsFailure(
                "JsonError",
                "Invalid JSON format in fragment files",
                fragmentsPath,
                ex
            );
            _logger.LogError(
                "{Message}: {Exception}",
                LoggingUtility.SanitizeForLog(failure.Message),
                LoggingUtility.SanitizeForLog(ex.ToString())
            );
            diagnostics.Add(
                new(ClaimsCompositionDiagnosticCode.MalformedFragment, sourcePath, JsonPath: ex.Path ?? "")
            );
            return new(new ClaimsLoadResult(null, [failure]), diagnostics.ToArray());
        }
        catch (DirectoryNotFoundException ex)
        {
            ClaimsFailure failure = new ClaimsFailure(
                "DirectoryNotFound",
                "Fragment directory not found",
                fragmentsPath,
                ex
            );
            _logger.LogError(
                "{Message}: {Exception}",
                LoggingUtility.SanitizeForLog(failure.Message),
                LoggingUtility.SanitizeForLog(ex.ToString())
            );
            diagnostics.Add(new(ClaimsCompositionDiagnosticCode.FragmentReadFailed, sourcePath));
            return new(new ClaimsLoadResult(null, [failure]), diagnostics.ToArray());
        }
        catch (FileNotFoundException ex)
        {
            ClaimsFailure failure = new ClaimsFailure(
                "FileNotFound",
                "Fragment file not found",
                fragmentsPath,
                ex
            );
            _logger.LogError(
                "{Message}: {Exception}",
                LoggingUtility.SanitizeForLog(failure.Message),
                LoggingUtility.SanitizeForLog(ex.ToString())
            );
            diagnostics.Add(new(ClaimsCompositionDiagnosticCode.FragmentReadFailed, sourcePath));
            return new(new ClaimsLoadResult(null, [failure]), diagnostics.ToArray());
        }
        catch (IOException ex)
        {
            ClaimsFailure failure = new ClaimsFailure(
                "IOError",
                "Failed to read fragment files",
                fragmentsPath,
                ex
            );
            _logger.LogError(
                "{Message}: {Exception}",
                LoggingUtility.SanitizeForLog(failure.Message),
                LoggingUtility.SanitizeForLog(ex.ToString())
            );
            diagnostics.Add(new(ClaimsCompositionDiagnosticCode.FragmentReadFailed, sourcePath));
            return new(new ClaimsLoadResult(null, [failure]), diagnostics.ToArray());
        }
        catch (UnauthorizedAccessException ex)
        {
            diagnostics.Add(new(ClaimsCompositionDiagnosticCode.FragmentReadFailed, sourcePath));
            return new(
                new ClaimsLoadResult(
                    null,
                    [
                        new ClaimsFailure(
                            "UnauthorizedAccess",
                            "Access denied reading fragment file",
                            fragmentsPath,
                            ex
                        ),
                    ]
                ),
                diagnostics.ToArray()
            );
        }
    }

    /// <summary>
    /// Discovers fragment files matching the pattern *-claims.json in the specified directory
    /// </summary>
    public List<string> DiscoverFragmentFiles(string fragmentsPath) =>
        DiscoverFragmentFiles(fragmentsPath, []);

    [SuppressMessage(
        "Major Code Smell",
        "S6667",
        Justification = "Raw exception text may contain input paths or JSON; log sanitized text and retain the original exception only in the structured result."
    )]
    private List<string> DiscoverFragmentFiles(
        string fragmentsPath,
        List<ClaimsCompositionDiagnostic> diagnostics
    )
    {
        try
        {
            if (!_fileSystem.DirectoryExists(fragmentsPath))
            {
                diagnostics.Add(new(ClaimsCompositionDiagnosticCode.MissingDirectory, fragmentsPath));
                _logger.LogWarning(
                    "Fragment path does not exist: {FragmentsPath}",
                    LoggingUtility.SanitizeForLog(fragmentsPath)
                );
                return [];
            }

            List<string> fragmentFiles = _fileSystem
                .GetFiles(fragmentsPath, "*-claims.json")
                .Where(f => !Path.GetFileName(f).Equals("Claims.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            // Fragments were named *-claimset.json before 8.1; they are not read, so say so
            foreach (
                string retiredFile in _fileSystem.GetFiles(fragmentsPath, "*-claimset.json").OrderBy(f => f)
            )
            {
                diagnostics.Add(new(ClaimsCompositionDiagnosticCode.RetiredFragment, retiredFile));
                _logger.LogWarning(
                    "Claims fragment file {FilePath} is not read: fragment file names must end in -claims.json. Rename the file to apply it.",
                    LoggingUtility.SanitizeForLog(retiredFile)
                );
            }

            if (fragmentFiles.Count == 0 && diagnostics.Count == 0)
            {
                diagnostics.Add(new(ClaimsCompositionDiagnosticCode.EmptyDirectory, fragmentsPath));
            }

            _logger.LogDebug(
                "Discovered {Count} fragment files in {Path}",
                fragmentFiles.Count,
                LoggingUtility.SanitizeForLog(fragmentsPath)
            );
            return fragmentFiles;
        }
        catch (DirectoryNotFoundException ex)
        {
            diagnostics.Add(new(ClaimsCompositionDiagnosticCode.DirectoryDiscoveryFailed, fragmentsPath));
            _logger.LogError(
                "Directory not found when discovering fragment files: {FragmentsPath}: {Exception}",
                LoggingUtility.SanitizeForLog(fragmentsPath),
                LoggingUtility.SanitizeForLog(ex.ToString())
            );
            return [];
        }
        catch (IOException ex)
        {
            diagnostics.Add(new(ClaimsCompositionDiagnosticCode.DirectoryDiscoveryFailed, fragmentsPath));
            _logger.LogError(
                "I/O error when discovering fragment files in path: {FragmentsPath}: {Exception}",
                LoggingUtility.SanitizeForLog(fragmentsPath),
                LoggingUtility.SanitizeForLog(ex.ToString())
            );
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            diagnostics.Add(new(ClaimsCompositionDiagnosticCode.DirectoryDiscoveryFailed, fragmentsPath));
            _logger.LogError(
                "Access denied when discovering fragment files in path: {FragmentsPath}: {Exception}",
                LoggingUtility.SanitizeForLog(fragmentsPath),
                LoggingUtility.SanitizeForLog(ex.ToString())
            );
            return [];
        }
    }

    /// <summary>
    /// Converts hierarchy JSON to mutable models used while applying claim-set fragments.
    /// </summary>
    private static List<TransformationClaim> ConvertToTransformationModel(JsonNode claimsHierarchyNode)
    {
        string hierarchyJson = claimsHierarchyNode.ToJsonString();
        return JsonSerializer.Deserialize<List<TransformationClaim>>(hierarchyJson, _jsonOptions) ?? [];
    }

    /// <summary>
    /// Converts transformation model back to JsonNode format
    /// </summary>
    private static JsonNode ConvertFromTransformationModel(List<TransformationClaim> claims)
    {
        string json = JsonSerializer.Serialize(claims, _outputJsonOptions);
        return JsonNode.Parse(json) ?? JsonNode.Parse("[]")!;
    }

    /// <summary>
    /// Copies the base claim sets and appends, as system-reserved, each fragment-defined claim set
    /// that the base does not already declare
    /// </summary>
    private JsonNode WithDefinedClaimSets(
        JsonNode? baseClaimSetsNode,
        List<ClaimSetDefinition> definitions,
        List<ClaimsCompositionDiagnostic> diagnostics
    )
    {
        // Re-parse so the composed document never shares nodes with the base document
        JsonArray claimSets = JsonNode.Parse(baseClaimSetsNode?.ToJsonString() ?? "[]")!.AsArray();

        HashSet<string> declaredClaimSetNames = new(
            claimSets.Select(claimSet => claimSet?["claimSetName"]?.GetValue<string>()).OfType<string>(),
            StringComparer.OrdinalIgnoreCase
        );

        foreach (ClaimSetDefinition definition in definitions.Where(d => declaredClaimSetNames.Add(d.Name)))
        {
            string claimSetName = definition.Name;
            diagnostics.Add(
                new(
                    ClaimsCompositionDiagnosticCode.ClaimSetRegistered,
                    definition.SourcePath,
                    ClaimSetName: claimSetName,
                    JsonPath: definition.JsonPath
                )
            );
            claimSets.Add(new JsonObject { ["claimSetName"] = claimSetName, ["isSystemReserved"] = true });
            _logger.LogInformation(
                "Registered claim set {ClaimSetName} defined by a claims fragment",
                LoggingUtility.SanitizeForLog(claimSetName)
            );
        }

        return claimSets;
    }

    /// <summary>
    /// Applies a fragment's parent claims and claim-set grants to the existing hierarchy.
    /// Returns the claim set name the fragment defines, or null when it defines none. A fragment
    /// defines its top-level name (or file name) as a claim set when it has a non-parent resource
    /// claim, because those grants are attached under that name.
    /// </summary>
    private ClaimSetDefinition? ApplyFragmentTransformation(
        string fragmentFilePath,
        List<TransformationClaim> existingClaims,
        List<ClaimsCompositionDiagnostic> diagnostics
    )
    {
        string jsonData = _fileSystem.ReadAllText(fragmentFilePath);
        ClaimSetResourceClaims claimSetData = JsonSerializer.Deserialize<ClaimSetResourceClaims>(
            jsonData,
            _jsonOptions
        )!;
        if (claimSetData is null || claimSetData.ResourceClaims is null)
        {
            diagnostics.Add(
                new(
                    ClaimsCompositionDiagnosticCode.NoResourceClaims,
                    fragmentFilePath,
                    ClaimSetName: claimSetData?.Name ?? "",
                    JsonPath: "$.resourceClaims"
                )
            );
            _logger.LogWarning(
                "Fragment file {FilePath} has no resource claims",
                LoggingUtility.SanitizeForLog(fragmentFilePath)
            );
            return null;
        }

        string claimSetName = claimSetData.Name ?? Path.GetFileNameWithoutExtension(fragmentFilePath);
        bool definesClaimSet = false;
        for (int index = 0; index < claimSetData.ResourceClaims.Count; index++)
        {
            ResourceClaim resourceClaim = claimSetData.ResourceClaims[index];
            ClaimsCompositionDiagnostic context = new(
                ClaimsCompositionDiagnosticCode.UnappliedPolicyField,
                fragmentFilePath,
                resourceClaim.Name ?? "",
                claimSetName,
                $"$.resourceClaims[{index}]"
            );
            if (resourceClaim.IsParent)
            {
                ApplyParentResourceClaim(resourceClaim, existingClaims, context, diagnostics);
            }
            else
            {
                DiagnoseIgnoredPolicy(resourceClaim, context, diagnostics);
                ApplyChildResourceClaim(
                    fragmentFilePath,
                    resourceClaim,
                    existingClaims,
                    claimSetName,
                    context,
                    diagnostics
                );
                definesClaimSet = true;
            }
        }

        string namePath = claimSetData.Name is null ? "" : "$.name";
        return definesClaimSet ? new ClaimSetDefinition(claimSetName, fragmentFilePath, namePath) : null;
    }

    /// <summary>
    /// Applies parent resource claim transformation
    /// </summary>
    private static void ApplyParentResourceClaim(
        ResourceClaim resourceClaim,
        List<TransformationClaim> existingClaims,
        ClaimsCompositionDiagnostic context,
        List<ClaimsCompositionDiagnostic> diagnostics
    )
    {
        DiagnoseIgnoredCollection(
            resourceClaim.AuthorizationStrategyOverridesForCRUD?.Count ?? 0,
            "authorizationStrategyOverridesForCRUD",
            context,
            diagnostics
        );
        for (int index = 0; index < resourceClaim.Children.Count; index++)
        {
            ResourceClaim child = resourceClaim.Children[index];
            ClaimsCompositionDiagnostic childContext = context with
            {
                ClaimName = child.Name ?? "",
                JsonPath = $"{context.JsonPath}.children[{index}]",
            };
            DiagnoseIgnoredPolicy(child, childContext, diagnostics);
            DiagnoseIgnoredCollection(
                child.AuthorizationStrategyOverridesForCRUD?.Count ?? 0,
                "authorizationStrategyOverridesForCRUD",
                childContext,
                diagnostics
            );
        }
        if (FindClaim(existingClaims, resourceClaim.Name!) is TransformationClaim existingClaim)
        {
            DiagnoseIgnoredCollection(resourceClaim.ClaimSets?.Count ?? 0, "claimSets", context, diagnostics);
            DiagnoseIgnoredCollection(
                resourceClaim.DefaultAuthorizationStrategiesForCRUD?.Count ?? 0,
                "_defaultAuthorizationStrategiesForCrud",
                context,
                diagnostics
            );
            for (int index = 0; index < resourceClaim.Children.Count; index++)
            {
                string childPath = $"{context.JsonPath}.children[{index}]";
                if (string.IsNullOrEmpty(resourceClaim.Children[index].Name))
                {
                    diagnostics.Add(
                        context with
                        {
                            Code = ClaimsCompositionDiagnosticCode.InvalidChildName,
                            ClaimName = existingClaim.Name ?? "",
                            JsonPath = $"{childPath}.name",
                        }
                    );
                }
                else if (existingClaim.Claims is null)
                {
                    diagnostics.Add(
                        context with
                        {
                            Code = ClaimsCompositionDiagnosticCode.ChildCollectionMissing,
                            ClaimName = existingClaim.Name ?? "",
                            JsonPath = childPath,
                        }
                    );
                }
            }
            // Add additional child claims
            List<TransformationClaim> childClaims = resourceClaim
                .Children.Where(x => !string.IsNullOrEmpty(x.Name))
                .Select(x => new TransformationClaim { Name = x.Name! })
                .ToList();
            if (childClaims.Count > 0)
            {
                existingClaim.Claims?.AddRange(childClaims);
            }
        }
        else
        {
            if (resourceClaim.DefaultAuthorizationStrategiesForCRUD is not null)
            {
                DiagnoseNullPolicyEntries(
                    resourceClaim.DefaultAuthorizationStrategiesForCRUD,
                    context with
                    {
                        JsonPath = $"{context.JsonPath}._defaultAuthorizationStrategiesForCrud",
                    },
                    diagnostics
                );
            }
            // Add claim at root level
            existingClaims.Add(
                new TransformationClaim
                {
                    Name = resourceClaim.Name,
                    Claims =
                        resourceClaim.Children.Count > 0
                            ?
                            [
                                .. resourceClaim.Children.Select(x => new TransformationClaim
                                {
                                    Name = x.Name,
                                }),
                            ]
                            : null,
                    ClaimSets =
                        resourceClaim.ClaimSets?.Count > 0
                            ?
                            [
                                .. resourceClaim.ClaimSets.Select(cs => new TransformationClaimSet
                                {
                                    Name = cs.Name,
                                    Actions =
                                        cs.Actions?.Count > 0
                                            ? cs
                                                .Actions.Select(a => new TransformationClaimSetAction
                                                {
                                                    Name = a.Name,
                                                    AuthorizationStrategyOverrides =
                                                        a.AuthorizationStrategyOverrides?.Count > 0
                                                            ? a
                                                                .AuthorizationStrategyOverrides.Select(
                                                                    auth => new TransformationAuthorizationStrategy
                                                                    {
                                                                        Name = auth.Name,
                                                                    }
                                                                )
                                                                .ToList()
                                                            : null,
                                                })
                                                .ToList()
                                            : null,
                                }),
                            ]
                            : null,
                    DefaultAuthorization =
                        resourceClaim.DefaultAuthorizationStrategiesForCRUD?.Count > 0
                            ? new TransformationDefaultAuthorization
                            {
                                Actions = resourceClaim
                                    .DefaultAuthorizationStrategiesForCRUD.OfType<ClaimSetResourceClaimActionAuthStrategies>()
                                    .Select(x => new TransformationAction
                                    {
                                        Name = x.ActionName,
                                        AuthorizationStrategies =
                                            x.AuthorizationStrategies is not null
                                            && x.AuthorizationStrategies.Any()
                                                ? x
                                                    .AuthorizationStrategies.Select(
                                                        auth => new TransformationAuthorizationStrategy
                                                        {
                                                            Name = auth.Name,
                                                        }
                                                    )
                                                    .ToList()
                                                : null,
                                    })
                                    .ToList(),
                            }
                            : null,
                }
            );
        }
    }

    /// <summary>
    /// Applies child resource claim transformation. The grant name is resolved exactly as written
    /// first, and only when no claim has that name is its singular form tried. When neither matches,
    /// a warning is logged and the entry is skipped.
    /// </summary>
    private void ApplyChildResourceClaim(
        string fragmentFilePath,
        ResourceClaim resourceClaim,
        List<TransformationClaim> existingClaims,
        string claimSetName,
        ClaimsCompositionDiagnostic context,
        List<ClaimsCompositionDiagnostic> diagnostics
    )
    {
        string name = resourceClaim.Name!;
        if (FindGrantClaim(existingClaims, name) is not TransformationClaim existingClaim)
        {
            diagnostics.Add(
                new(
                    ClaimsCompositionDiagnosticCode.UnmatchedGrantTarget,
                    fragmentFilePath,
                    name,
                    claimSetName,
                    $"{context.JsonPath}.name"
                )
            );
            _logger.LogWarning(
                "Fragment {FragmentFile} grants claim set {ClaimSetName} access to {ResourceClaimName}, which matches no resource claim by that name or its singular form; the entry is skipped",
                LoggingUtility.SanitizeForLog(fragmentFilePath),
                LoggingUtility.SanitizeForLog(claimSetName),
                LoggingUtility.SanitizeForLog(name)
            );
        }
        else
        {
            if (resourceClaim.AuthorizationStrategyOverridesForCRUD is not null)
            {
                DiagnoseNullPolicyEntries(
                    resourceClaim.AuthorizationStrategyOverridesForCRUD,
                    context with
                    {
                        JsonPath = $"{context.JsonPath}.authorizationStrategyOverridesForCRUD",
                    },
                    diagnostics
                );
            }
            List<TransformationClaimSetAction> actions = new List<TransformationClaimSetAction>();
            foreach (
                ClaimSetResourceClaimActionAuthStrategies actionAuthStrategy in resourceClaim.AuthorizationStrategyOverridesForCRUD?.OfType<ClaimSetResourceClaimActionAuthStrategies>()
                    ?? []
            )
            {
                actions.Add(
                    new TransformationClaimSetAction
                    {
                        Name = actionAuthStrategy.ActionName!,
                        AuthorizationStrategyOverrides =
                            actionAuthStrategy.AuthorizationStrategies is not null
                            && actionAuthStrategy.AuthorizationStrategies.Any()
                                ? actionAuthStrategy
                                    .AuthorizationStrategies.Select(
                                        auth => new TransformationAuthorizationStrategy { Name = auth.Name }
                                    )
                                    .ToList()
                                : null,
                    }
                );
            }

            // Only create ClaimSets array and add claimSet if there are actions to include
            if (actions.Count > 0)
            {
                existingClaim.ClaimSets ??= [];
                if (
                    !existingClaim.ClaimSets.Exists(x =>
                        string.Equals(x.Name, claimSetName, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    existingClaim.ClaimSets.Add(
                        new TransformationClaimSet { Name = claimSetName, Actions = actions }
                    );
                }
            }
        }
    }

    // These fields are discarded on non-parent entries and on children copied by name only.
    private static void DiagnoseIgnoredPolicy(
        ResourceClaim resourceClaim,
        ClaimsCompositionDiagnostic context,
        List<ClaimsCompositionDiagnostic> diagnostics
    )
    {
        DiagnoseIgnoredCollection(resourceClaim.Children?.Count ?? 0, "children", context, diagnostics);
        DiagnoseIgnoredCollection(resourceClaim.ClaimSets?.Count ?? 0, "claimSets", context, diagnostics);
        DiagnoseIgnoredCollection(
            resourceClaim.DefaultAuthorizationStrategiesForCRUD?.Count ?? 0,
            "_defaultAuthorizationStrategiesForCrud",
            context,
            diagnostics
        );
    }

    private static void DiagnoseIgnoredCollection(
        int count,
        string field,
        ClaimsCompositionDiagnostic context,
        List<ClaimsCompositionDiagnostic> diagnostics
    )
    {
        if (count > 0)
        {
            diagnostics.Add(context with { JsonPath = $"{context.JsonPath}.{field}" });
        }
    }

    private static void DiagnoseNullPolicyEntries(
        List<ClaimSetResourceClaimActionAuthStrategies?> entries,
        ClaimsCompositionDiagnostic context,
        List<ClaimsCompositionDiagnostic> diagnostics
    )
    {
        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index] is null)
            {
                diagnostics.Add(
                    context with
                    {
                        Code = ClaimsCompositionDiagnosticCode.NullPolicyEntry,
                        JsonPath = $"{context.JsonPath}[{index}]",
                    }
                );
            }
        }
    }

    // Exact name first; the singular form only when no claim has the exact name
    private static TransformationClaim? FindGrantClaim(List<TransformationClaim> existingClaims, string name)
    {
        string singularName = PluralToSingular(name);
        return FindClaim(existingClaims, name)
            ?? (singularName == name ? null : FindClaim(existingClaims, singularName));
    }

    private static TransformationClaim? FindClaim(List<TransformationClaim> existingClaims, string name) =>
        existingClaims.SelectMany(x => SearchRecursive(x, name)).FirstOrDefault();

    /// <summary>
    /// Recursively finds claims by resource name, matching the final two URI path segments
    /// case-insensitively when the claim name is an absolute URI.
    /// </summary>
    private static IEnumerable<TransformationClaim> SearchRecursive(
        TransformationClaim claim,
        string searchTerm
    )
    {
        if (string.Equals(ResourceName(claim.Name), searchTerm, StringComparison.InvariantCultureIgnoreCase))
        {
            yield return claim;
        }

        if (claim.Claims is not null)
        {
            foreach (TransformationClaim child in claim.Claims)
            {
                foreach (TransformationClaim match in SearchRecursive(child, searchTerm))
                {
                    yield return match;
                }
            }
        }

        static string ResourceName(string? resourceUri)
        {
            if (string.IsNullOrEmpty(resourceUri))
            {
                return string.Empty;
            }

            if (Uri.TryCreate(resourceUri, UriKind.Absolute, out var uri))
            {
                string[] pathSegments = uri.AbsolutePath.Split('/');
                return string.Join("/", pathSegments[^2..]);
            }

            return resourceUri;
        }
    }

    /// <summary>
    /// Normalizes fragment resource names for hierarchy lookup by replacing a trailing
    /// "ies" with "y" or removing a trailing "s".
    /// </summary>
    private static string PluralToSingular(string resourceClaimName)
    {
        if (
            resourceClaimName.EndsWith("ies", StringComparison.OrdinalIgnoreCase)
            && resourceClaimName.Length > 3
        )
        {
            return resourceClaimName[..^3] + "y";
        }

        if (
            resourceClaimName.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && resourceClaimName.Length > 1
        )
        {
            return resourceClaimName[..^1];
        }

        return resourceClaimName;
    }

    /// <summary>
    /// Cleans up empty collections that would violate JSON Schema minItems constraints
    /// Sets them to null so JsonIgnoreCondition.WhenWritingNull will omit them
    /// </summary>
    private static void CleanupEmptyCollections(List<TransformationClaim> claims)
    {
        foreach (TransformationClaim claim in claims)
        {
            // Clean up empty collections that violate minItems: 1
            if (claim.ClaimSets?.Count == 0)
            {
                claim.ClaimSets = null;
            }

            if (claim.DefaultAuthorization?.Actions?.Count == 0)
            {
                claim.DefaultAuthorization.Actions = null;
            }

            // If DefaultAuthorization has no actions, remove it entirely
            if (claim.DefaultAuthorization?.Actions is null)
            {
                claim.DefaultAuthorization = null;
            }

            // Clean nested claims recursively
            if (claim.Claims is not null)
            {
                CleanupEmptyCollections(claim.Claims);
                if (claim.Claims.Count == 0)
                {
                    claim.Claims = null;
                }
            }

            // Clean claim set actions
            if (claim.ClaimSets is not null)
            {
                foreach (TransformationClaimSet claimSet in claim.ClaimSets)
                {
                    if (claimSet.Actions?.Count == 0)
                    {
                        claimSet.Actions = null;
                    }
                }

                // Remove claim sets that have no actions (violates schema requirements)
                claim.ClaimSets.RemoveAll(cs => cs.Actions is null);

                // If no claim sets remain, set to null
                if (claim.ClaimSets.Count == 0)
                {
                    claim.ClaimSets = null;
                }
            }
        }
    }

    private sealed record ClaimSetDefinition(string Name, string SourcePath, string JsonPath);

    #region Internal Hierarchy Models and Fragment Input Records

    private sealed class TransformationClaim
    {
        public string? Name { get; set; }
        public TransformationDefaultAuthorization? DefaultAuthorization { get; set; }
        public List<TransformationClaimSet>? ClaimSets { get; set; }
        public List<TransformationClaim>? Claims { get; set; }
    }

    private sealed class TransformationDefaultAuthorization
    {
        public List<TransformationAction>? Actions { get; set; }
    }

    private sealed class TransformationAction
    {
        public string? Name { get; set; }
        public List<TransformationAuthorizationStrategy>? AuthorizationStrategies { get; set; }
    }

    private sealed class TransformationClaimSet
    {
        public string? Name { get; set; }
        public List<TransformationClaimSetAction>? Actions { get; set; }
    }

    private sealed class TransformationClaimSetAction
    {
        public string? Name { get; set; }
        public List<TransformationAuthorizationStrategy>? AuthorizationStrategyOverrides { get; set; }
    }

    private sealed class TransformationAuthorizationStrategy
    {
        public string? Name { get; set; }
    }

    // Input records for fragment resource claims, CRUD strategies, and claim-set names.
    // Positional records — the primary constructor is what System.Text.Json calls during
    // deserialization, satisfying analyzers that don't track reflection-based property writes.
    private sealed record ResourceClaim(string? Name = null, bool IsParent = false)
    {
        [JsonPropertyName("_defaultAuthorizationStrategiesForCrud")]
        public List<ClaimSetResourceClaimActionAuthStrategies?> DefaultAuthorizationStrategiesForCRUD { get; init; } =
        [];
        public List<ClaimSetResourceClaimActionAuthStrategies?> AuthorizationStrategyOverridesForCRUD { get; init; } =
        [];
        public List<ResourceClaim> Children { get; init; } = [];
        public List<TransformationClaimSet> ClaimSets { get; init; } = [];
    }

    private sealed record ClaimSetResourceClaimActionAuthStrategies(
        string? ActionName = null,
        IEnumerable<TransformationAuthorizationStrategy>? AuthorizationStrategies = null
    );

    private sealed record ClaimSetResourceClaims(string? Name = null)
    {
        // Preserve omission for the NoResourceClaims guard; an explicitly empty collection is valid.
        public List<ResourceClaim> ResourceClaims { get; init; } = null!;
    }

    #endregion
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

/// <summary>
/// DMS-1440 acceptance criteria 3, 14 and 15: the Configuration Service reads the projection over HTTP only. The backend
/// project, everything it builds on and its compiled assembly name no DMS assembly and no database provider; the
/// projection reader's types depend on no database API; and no Configuration Service product source names a DMS table
/// or <c>EffectiveSchema</c>. The sources are found by walking up from the test output directory.
/// </summary>
public partial class BackendProjectBoundaryTests
{
    private const string BackendProjectName = "EdFi.DmsConfigurationService.Backend";

    /// <summary>DMS assemblies, and the database providers the Configuration Service uses for its own database.</summary>
    private static readonly string[] _forbidden =
    [
        "EdFi.DataManagementService",
        "Npgsql",
        "Microsoft.Data.SqlClient",
    ];

    private static readonly Lazy<string> _configurationServiceRoot = new(FindConfigurationServiceRoot);

    private static string ConfigurationServiceRoot => _configurationServiceRoot.Value;

    private static string BackendDirectory =>
        Path.Combine(ConfigurationServiceRoot, "backend", BackendProjectName);

    private static string BackendProjectFile =>
        Path.Combine(BackendDirectory, BackendProjectName + ".csproj");

    private static Assembly BackendAssembly => typeof(EducationOrganizationProjectionReader).Assembly;

    private static bool IsForbidden(string name) =>
        Array.Exists(_forbidden, forbidden => name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ForbiddenMentions(string text) =>
        _forbidden.Where(forbidden => text.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

    private static bool IsInsideConfigurationService(string path) =>
        path.StartsWith(
            ConfigurationServiceRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>The full paths of the project files a project file references directly.</summary>
    private static IEnumerable<string> ProjectReferences(string projectFile) =>
        XDocument
            .Load(projectFile)
            .Descendants("ProjectReference")
            .Select(reference =>
                Path.GetFullPath(
                    Path.Combine(
                        Path.GetDirectoryName(projectFile)!,
                        reference.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar)
                    )
                )
            );

    /// <summary>Every project file a project file references, directly or through another project.</summary>
    private static IReadOnlyList<string> ProjectReferenceClosure(string projectFile)
    {
        string start = Path.GetFullPath(projectFile);
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> pending = new([start]);
        while (pending.TryDequeue(out string? current))
        {
            if (visited.Add(current))
            {
                foreach (string reference in ProjectReferences(current))
                {
                    pending.Enqueue(reference);
                }
            }
        }
        visited.Remove(start);
        return [.. visited.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static string FindConfigurationServiceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (
                File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "backend",
                        BackendProjectName,
                        BackendProjectName + ".csproj"
                    )
                )
            )
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "The Configuration Service source folder was not found above the test directory."
        );
    }

    [TestFixture]
    public class Given_the_backend_project_file
    {
        [Test]
        public void It_names_no_dms_assembly_or_database_provider() =>
            ForbiddenMentions(File.ReadAllText(BackendProjectFile)).Should().BeEmpty();

        [Test]
        public void It_references_projects_inside_the_configuration_service_only() =>
            ProjectReferences(BackendProjectFile)
                .Where(reference => !IsInsideConfigurationService(reference))
                .Should()
                .BeEmpty();
    }

    [TestFixture]
    public class Given_the_projects_the_backend_builds_on
    {
        private IReadOnlyList<string> _projects = null!;

        [SetUp]
        public void Setup() => _projects = ProjectReferenceClosure(BackendProjectFile);

        [Test]
        public void It_reaches_the_data_model_and_secrets_projects() =>
            _projects
                .Select(Path.GetFileName)
                .Should()
                .Contain([
                    "EdFi.DmsConfigurationService.DataModel.csproj",
                    "EdFi.DmsConfigurationService.Secrets.csproj",
                ]);

        [Test]
        public void It_finds_no_dms_assembly_or_database_provider_in_any_of_them() =>
            _projects
                .SelectMany(project =>
                    ForbiddenMentions(File.ReadAllText(project))
                        .Select(mention => $"{Path.GetFileName(project)}: {mention}")
                )
                .Should()
                .BeEmpty();

        [Test]
        public void It_finds_them_all_inside_the_configuration_service() =>
            _projects.Where(project => !IsInsideConfigurationService(project)).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_backend_lock_file
    {
        private List<string> _packages = null!;

        [SetUp]
        public void Setup()
        {
            using JsonDocument lockFile = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(BackendDirectory, "packages.lock.json"))
            );
            _packages =
            [
                .. lockFile
                    .RootElement.GetProperty("dependencies")
                    .EnumerateObject()
                    .SelectMany(framework => framework.Value.EnumerateObject())
                    .Select(package => package.Name),
            ];
        }

        [Test]
        public void It_lists_the_backend_dependencies_and_projects() =>
            _packages
                .Should()
                .Contain(["Microsoft.Extensions.Http", "edfi.dmsconfigurationservice.datamodel"]);

        [Test]
        public void It_resolves_no_dms_assembly_or_database_provider_even_transitively() =>
            _packages.Where(IsForbidden).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_compiled_backend_assembly
    {
        private List<string> _references = null!;

        [SetUp]
        public void Setup() =>
            _references = [.. BackendAssembly.GetReferencedAssemblies().Select(reference => reference.Name!)];

        [Test]
        public void It_references_the_data_model() =>
            _references.Should().Contain("EdFi.DmsConfigurationService.DataModel");

        [Test]
        public void It_references_no_dms_assembly_or_database_provider() =>
            _references.Where(IsForbidden).Should().BeEmpty();
    }

    /// <summary>
    /// Acceptance criteria 3 and 15: nothing in the projection reader opens a database. The Configuration Service's own
    /// database access (ADO.NET, its repositories and its job sessions) appears in no signature, field or source of
    /// the reader's namespace, compiler-generated types included.
    /// </summary>
    [TestFixture]
    public partial class Given_the_projection_reader_types
    {
        private static readonly Type[] _databaseTypes =
        [
            typeof(ICmsTransaction),
            typeof(JobDatabaseSession),
            typeof(IJobRepository),
            typeof(IJobEnqueuer),
            typeof(IJobFence),
        ];

        private HashSet<Type> _used = null!;

        [SetUp]
        public void Setup()
        {
            string projectionNamespace = typeof(EducationOrganizationProjectionReader).Namespace!;
            const BindingFlags Declared =
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly;

            _used = [];
            foreach (
                Type type in BackendAssembly.GetTypes().Where(type => type.Namespace == projectionNamespace)
            )
            {
                IEnumerable<Type> direct = new[] { type.BaseType }
                    .OfType<Type>()
                    .Concat(type.GetInterfaces())
                    .Concat(type.GetFields(Declared).Select(field => field.FieldType))
                    .Concat(type.GetProperties(Declared).Select(property => property.PropertyType))
                    .Concat(
                        type.GetConstructors(Declared)
                            .SelectMany(constructor => constructor.GetParameters())
                            .Select(parameter => parameter.ParameterType)
                    )
                    .Concat(
                        type.GetMethods(Declared)
                            .SelectMany(method =>
                                method
                                    .GetParameters()
                                    .Select(parameter => parameter.ParameterType)
                                    .Append(method.ReturnType)
                            )
                    );
                foreach (Type used in direct)
                {
                    Add(used);
                }
            }
        }

        [Test]
        public void It_sees_the_readers_http_dependencies() =>
            _used.Should().Contain([typeof(IHttpClientFactory), typeof(HttpRequestMessage)]);

        [Test]
        public void It_uses_no_database_api_in_any_signature_or_field() =>
            _used
                .Where(type =>
                    type.Namespace?.StartsWith("System.Data", StringComparison.Ordinal) == true
                    || type.Namespace == "EdFi.DmsConfigurationService.Backend.Repositories"
                    || IsForbidden(type.Assembly.GetName().Name!)
                    || _databaseTypes.Contains(type)
                )
                .Select(type => type.FullName)
                .Should()
                .BeEmpty();

        [Test]
        public void It_has_no_source_naming_a_database_api() =>
            Directory
                .EnumerateFiles(Path.Combine(BackendDirectory, "EducationOrganizationProjection"), "*.cs")
                .SelectMany(file =>
                    File.ReadLines(file)
                        .Select((line, index) => (line, index))
                        .Where(numbered => DatabaseApi().IsMatch(numbered.line))
                        .Select(numbered =>
                            $"{Path.GetFileName(file)}:{numbered.index + 1}: {numbered.line.Trim()}"
                        )
                )
                .Should()
                .BeEmpty();

        /// <summary>Adds a type with its element type and generic arguments, recursively.</summary>
        private void Add(Type type)
        {
            if (!_used.Add(type))
            {
                return;
            }
            if (type.HasElementType)
            {
                Add(type.GetElementType()!);
            }
            if (type.IsGenericType)
            {
                foreach (Type argument in type.GetGenericArguments())
                {
                    Add(argument);
                }
            }
        }

        [GeneratedRegex(
            @"\bSystem\.Data\b|\bDb(?:Connection|Command|DataSource|Transaction|DataReader)\b|\bNpgsql|\bSqlClient\b|\bEdFi\.DataManagementService\b|\bRepositories\b|\bConnectionString\b"
        )]
        private static partial Regex DatabaseApi();
    }

    /// <summary>
    /// Acceptance criterion 14: no Configuration Service product source (C# or SQL, test projects excluded) names a
    /// DMS table, the DMS schema or <c>EffectiveSchema</c>.
    /// </summary>
    [TestFixture]
    public partial class Given_the_configuration_service_product_sources
    {
        private List<string> _files = null!;

        [SetUp]
        public void Setup() =>
            _files = [
                .. Directory
                    .EnumerateFiles(ConfigurationServiceRoot, "*", SearchOption.AllDirectories)
                    .Where(file =>
                        file.EndsWith(".cs", StringComparison.Ordinal)
                        || file.EndsWith(".sql", StringComparison.Ordinal)
                    )
                    .Where(file =>
                        !Array.Exists(
                            Path.GetRelativePath(ConfigurationServiceRoot, file)
                                .Split(Path.DirectorySeparatorChar),
                            segment =>
                                segment is "bin" or "obj" or "tests"
                                || segment.Contains(".Tests", StringComparison.Ordinal)
                        )
                    ),
            ];

        [Test]
        public void It_scans_the_backend_code_and_the_deploy_scripts() =>
            _files
                .Select(Path.GetFileName)
                .Should()
                .Contain(["EducationOrganizationProjectionReader.cs", "0000_Create_CMS_Schema.sql"]);

        [Test]
        public void It_names_no_dms_table_or_effective_schema() =>
            _files
                .SelectMany(file =>
                    File.ReadLines(file)
                        .Select((line, index) => (line, index))
                        .Where(numbered => DmsSchemaObject().IsMatch(numbered.line))
                        .Select(numbered =>
                            $"{Path.GetRelativePath(ConfigurationServiceRoot, file)}:{numbered.index + 1}"
                        )
                )
                .Should()
                .BeEmpty();

        [GeneratedRegex(@"EffectiveSchema|\bdms\.(?:""|\[|[A-Z])|""dms""\.|\[dms\]\.")]
        private static partial Regex DmsSchemaObject();
    }
}

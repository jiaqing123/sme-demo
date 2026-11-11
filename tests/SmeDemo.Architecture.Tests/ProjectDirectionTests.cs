using System.Reflection;
using System.Xml.Linq;

namespace SmeDemo.Architecture.Tests;

/// <summary>
/// Enforces the reference-baseline dependency direction: the application and
/// domain layers are inner layers and must never point back at the adapters or
/// the host. Both the compiled assembly references and the MSBuild project
/// references are checked so an unused ProjectReference cannot quietly create
/// the dependency.
/// </summary>
[TestClass]
public sealed class ProjectDirectionTests
{
    [TestMethod]
    [DataRow("SmeDemo.Application", "SmeDemo.Api")]
    [DataRow("SmeDemo.Application", "SmeDemo.Infrastructure")]
    [DataRow("SmeDemo.Domain", "SmeDemo.Application")]
    [DataRow("SmeDemo.Domain", "SmeDemo.Infrastructure")]
    [DataRow("SmeDemo.Domain", "SmeDemo.Api")]
    public void InnerLayer_DoesNotReferenceOuterLayer(string layer, string forbiddenLayer)
    {
        var assembly = LayerAssembly(layer);
        var assemblyViolations = assembly.GetReferencedAssemblies()
            .Where(reference => string.Equals(reference.Name, forbiddenLayer, StringComparison.OrdinalIgnoreCase))
            .Select(reference => $"assembly reference {reference.FullName}")
            .ToArray();

        var projectPath = Path.Combine(RepositoryRoot(), LayerProjectPath(layer));
        var projectViolations = ReadProjectReferences(projectPath)
            .Where(reference => string.Equals(
                ResolveAssemblyName(reference),
                forbiddenLayer,
                StringComparison.OrdinalIgnoreCase))
            .Select(reference => $"project reference {reference}")
            .ToArray();

        var violations = assemblyViolations.Concat(projectViolations).ToArray();
        var actualReferences = string.Join(
            ", ",
            assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .OrderBy(name => name, StringComparer.Ordinal));

        Assert.AreEqual(
            0,
            violations.Length,
            $"{layer} is an inner layer and must not depend on {forbiddenLayer}. " +
            $"Offending references: {string.Join("; ", violations)}. " +
            $"Compiled assembly references: [{actualReferences}]. Project: {projectPath}.");
    }

    private static Assembly LayerAssembly(string layer) => layer switch
    {
        "SmeDemo.Application" => typeof(SmeDemo.Application.ServiceCollectionExtensions).Assembly,
        "SmeDemo.Domain" => typeof(SmeDemo.Domain.Entities.Card).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown layer."),
    };

    private static string LayerProjectPath(string layer) => layer switch
    {
        "SmeDemo.Application" => Path.Combine("src", "SmeDemo.Application", "SmeDemo.Application.csproj"),
        "SmeDemo.Domain" => Path.Combine("src", "SmeDemo.Domain", "SmeDemo.Domain.csproj"),
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown layer."),
    };

    private static IEnumerable<string> ReadProjectReferences(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException($"Project path has no directory: {projectPath}");
        var document = XDocument.Load(projectPath);

        return document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(Path.Combine(
                projectDirectory,
                include!.Replace('\\', Path.DirectorySeparatorChar))));
    }

    /// <summary>
    /// Resolves the assembly identity a ProjectReference produces, using the
    /// referenced project's AssemblyName property when it sets one.
    /// </summary>
    private static string ResolveAssemblyName(string projectPath)
    {
        if (!File.Exists(projectPath))
        {
            throw new InvalidOperationException(
                $"ProjectReference points at a missing project: {projectPath}");
        }

        var document = XDocument.Load(projectPath);
        var assemblyName = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "AssemblyName")
            ?.Value
            .Trim();

        return string.IsNullOrEmpty(assemblyName)
            ? Path.GetFileNameWithoutExtension(projectPath)
            : assemblyName;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SmeDemo.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find SmeDemo.slnx above the test output directory {AppContext.BaseDirectory}.");
    }
}

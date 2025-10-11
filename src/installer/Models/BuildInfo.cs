using System.Xml.Serialization;
using YamlDotNet.Serialization;

namespace SbinInstaller.Models;

/// <summary>
/// Represents the build-info.yaml metadata for package generation
/// </summary>
public class BuildInfo
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "version")]
    public string Version { get; set; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = string.Empty;

    [YamlMember(Alias = "author")]
    public string Author { get; set; } = string.Empty;

    [YamlMember(Alias = "license")]
    public string License { get; set; } = string.Empty;

    [YamlMember(Alias = "homepage")]
    public string Homepage { get; set; } = string.Empty;

    [YamlMember(Alias = "dependencies")]
    public List<string> Dependencies { get; set; } = new();

    [YamlMember(Alias = "target")]
    public string Target { get; set; } = "/";

    [YamlMember(Alias = "install_location")]
    public string InstallLocation { get; set; } = string.Empty;

    [YamlMember(Alias = "restart_action")]
    public string RestartAction { get; set; } = "None";
}

/// <summary>
/// Represents .nuspec metadata from NuGet packages (.nupkg files)
/// Note: Namespace is omitted to support all NuGet schema versions
/// (2010/07, 2011/08, 2011/10, 2012/06, 2013/01, etc.)
/// </summary>
[XmlRoot("package", Namespace = "")]
public class NuspecPackage
{
    [XmlElement("metadata")]
    public NuspecMetadata Metadata { get; set; } = new();

    [XmlArray("files")]
    [XmlArrayItem("file")]
    public List<NuspecFile> Files { get; set; } = new();
}

/// <summary>
/// Metadata section from .nuspec file
/// </summary>
public class NuspecMetadata
{
    [XmlElement("id")]
    public string Id { get; set; } = string.Empty;

    [XmlElement("version")]
    public string Version { get; set; } = string.Empty;

    [XmlElement("title")]
    public string Title { get; set; } = string.Empty;

    [XmlElement("authors")]
    public string Authors { get; set; } = string.Empty;

    [XmlElement("owners")]
    public string Owners { get; set; } = string.Empty;

    [XmlElement("description")]
    public string Description { get; set; } = string.Empty;

    [XmlElement("summary")]
    public string Summary { get; set; } = string.Empty;

    [XmlElement("releaseNotes")]
    public string ReleaseNotes { get; set; } = string.Empty;

    [XmlElement("copyright")]
    public string Copyright { get; set; } = string.Empty;

    [XmlElement("language")]
    public string Language { get; set; } = string.Empty;

    [XmlElement("tags")]
    public string Tags { get; set; } = string.Empty;

    [XmlElement("licenseUrl")]
    public string LicenseUrl { get; set; } = string.Empty;

    [XmlElement("license")]
    public NuspecLicense License { get; set; } = new();

    [XmlElement("projectUrl")]
    public string ProjectUrl { get; set; } = string.Empty;

    [XmlElement("iconUrl")]
    public string IconUrl { get; set; } = string.Empty;

    [XmlElement("requireLicenseAcceptance")]
    public bool RequireLicenseAcceptance { get; set; }

    [XmlArray("dependencies")]
    [XmlArrayItem("dependency")]
    public List<NuspecDependency> Dependencies { get; set; } = new();

    [XmlArray("frameworkDependencies")]
    [XmlArrayItem("frameworkDependency")]
    public List<NuspecFrameworkDependency> FrameworkDependencies { get; set; } = new();
}

/// <summary>
/// License information from .nuspec
/// </summary>
public class NuspecLicense
{
    [XmlAttribute("type")]
    public string Type { get; set; } = string.Empty;

    [XmlText]
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Dependency information from .nuspec
/// </summary>
public class NuspecDependency
{
    [XmlAttribute("id")]
    public string Id { get; set; } = string.Empty;

    [XmlAttribute("version")]
    public string Version { get; set; } = string.Empty;

    [XmlAttribute("include")]
    public string Include { get; set; } = string.Empty;

    [XmlAttribute("exclude")]
    public string Exclude { get; set; } = string.Empty;
}

/// <summary>
/// Framework dependency from .nuspec
/// </summary>
public class NuspecFrameworkDependency
{
    [XmlAttribute("assemblyName")]
    public string AssemblyName { get; set; } = string.Empty;

    [XmlAttribute("targetFramework")]
    public string TargetFramework { get; set; } = string.Empty;
}

/// <summary>
/// File information from .nuspec
/// </summary>
public class NuspecFile
{
    [XmlAttribute("src")]
    public string Source { get; set; } = string.Empty;

    [XmlAttribute("target")]
    public string Target { get; set; } = string.Empty;

    [XmlAttribute("exclude")]
    public string Exclude { get; set; } = string.Empty;
}
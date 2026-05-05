using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Arius.Core.Shared.Paths;
using Arius.Explorer.Shared.Extensions;

namespace Arius.Explorer.Settings;

public record RepositoryOptions
{
    public string LocalDirectoryPath { get; set; } = "";

    public string AccountName { get; set; } = "";
    public string AccountKeyProtected { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string PassphraseProtected { get; set; } = "";

    public DateTime LastOpened { get; set; }

    [JsonIgnore, XmlIgnore]
    public DirectoryInfo LocalDirectory => new(LocalDirectoryPath);

    [JsonIgnore, XmlIgnore]
    public LocalRootPath LocalRoot => LocalRootPath.Parse(LocalDirectoryPath);

    [JsonIgnore, XmlIgnore]
    public string AccountKey => string.IsNullOrEmpty(AccountKeyProtected) ? "" : AccountKeyProtected.Unprotect();

    [JsonIgnore, XmlIgnore]
    public string Passphrase => string.IsNullOrEmpty(PassphraseProtected) ? "" : PassphraseProtected.Unprotect();

    public override string ToString()
    {
        return $"{LocalDirectory.FullName} on {AccountName}:{ContainerName}";
    }
}

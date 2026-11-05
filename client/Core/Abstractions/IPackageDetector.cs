using client.Core.Models;

namespace client.Core.Abstractions;

public interface IPackageDetector
{
    IReadOnlyList<InstalledPackage> GetAllInstalledPackages();

    IReadOnlySet<string> KnownPackageNames { get; }

    bool IsKnownPackage(string packageName);

    IReadOnlyList<string> MissingPermissions { get; }

    IReadOnlyList<string> PermissionGrantInstructions { get; }

    void ForceRecheck();
}

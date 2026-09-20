using System.IO;

using System.Text;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Bitchat.Windows.Ble;

/// <summary>
/// Grants package identity to this unpackaged app via a sparse MSIX package
/// (Windows 10 2004+). Package identity is required for connectable BLE
/// peripheral advertising (GattServiceProvider).
/// </summary>
public static class SparsePackageIdentity
{
    public static bool HasIdentity()
    {
        try
        {
            var p = Package.Current;
            return p != null;
        }
        catch
        {
            return false;
        }
    }

    private const string IdentityName = "BitchatWindows";
    private const string ManifestXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
          <Identity Name="BitchatWindows" Version="1.0.0.0" ProcessorArchitecture="neutral" Publisher="CN=bitchat" />
          <Properties>
            <DisplayName>bitchat</DisplayName>
            <PublisherDisplayName>bitchat</PublisherDisplayName>
            <Logo>Assets\Square150x150Logo.png</Logo>
            <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
          </Properties>
          <Resources>
            <Resource Language="en-us" />
          </Resources>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
            <rescap:Capability Name="unvirtualizedResources" />
            <uap:Capability Name="bluetooth" />
          </Capabilities>
          <Applications>
            <Application Id="App" Executable="bitchat.exe" uap10:TrustLevel="fullTrust" uap10:RuntimeBehavior="fullTrustProcess">
              <uap:VisualElements AppListEntry="none" DisplayName="bitchat" Description="bitchat bluetooth mesh"
                                  BackgroundColor="#101418" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
            </Application>
          </Applications>
        </Package>
        """;

    // 1x1 transparent PNG
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    public static async Task<(bool Ok, string Message, bool IdentityNow)> TryRegisterAsync()
    {
        var appDir = AppContext.BaseDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(appDir, "Assets"));
            File.WriteAllText(Path.Combine(appDir, "AppxManifest.xml"), ManifestXml);
            File.WriteAllBytes(Path.Combine(appDir, "Assets", "Square44x44Logo.png"), Convert.FromBase64String(PngBase64));
            File.WriteAllBytes(Path.Combine(appDir, "Assets", "Square150x150Logo.png"), Convert.FromBase64String(PngBase64));

            var pm = new PackageManager();
            var external = appDir.EndsWith('\\') ? appDir : appDir + "\\";
            var options = new AddPackageOptions
            {
                AllowUnsigned = true,
                ExternalLocationUri = new Uri(external)
            };

            DeploymentResult result;
            try
            {
                result = await pm.AddPackageByUriAsync(
                    new Uri(Path.Combine(appDir, "AppxManifest.xml")),
                    options);
            }
            catch (Exception ex)
            {
                // Remove a previously registered copy and retry once.
                try
                {
                    foreach (var pkg in pm.FindPackagesForUser("", IdentityName))
                        await pm.RemovePackageAsync(pkg.Id.FullName);
                    result = await pm.AddPackageByUriAsync(
                        new Uri(Path.Combine(appDir, "AppxManifest.xml")),
                        options);
                }
                catch (Exception ex2)
                {
                    return (false, ex2.Message, false);
                }
            }

            var now = HasIdentity();
            return (true, result.ErrorText.Length > 0 ? result.ErrorText : "registered", now);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, false);
        }
    }

    /// <summary>Removes the sparse package (e.g. on uninstall).</summary>
    public static async Task<string> TryUnregisterAsync()
    {
        try
        {
            var pm = new PackageManager();
            foreach (var pkg in pm.FindPackagesForUser("", IdentityName))
                await pm.RemovePackageAsync(pkg.Id.FullName);
            return "removed";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

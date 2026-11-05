using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using client.Core;

namespace client.Services;

/// <summary>
/// Identity headers sent on every DeviceAuth HTTP/WS call so the server can refresh
/// <c>employee_devices.client_version</c> / platform after an in-place reinstall without
/// requiring a new employee-login (login is the only other writer of those columns).
/// </summary>
internal static class ClientIdentityHeaders
{
    public const string VersionHeader = "X-Client-Version";
    public const string PlatformHeader = "X-Client-Platform";

    public static string Version => AppInfo.Version;

    public static string Platform => RuntimeInformation.OSDescription;

    public static void Apply(HttpRequestHeaders headers)
    {
        headers.Remove(VersionHeader);
        headers.Remove(PlatformHeader);
        headers.TryAddWithoutValidation(VersionHeader, Version);
        headers.TryAddWithoutValidation(PlatformHeader, Platform);
    }

    public static void Apply(HttpRequestMessage request)
    {
        Apply(request.Headers);
    }

    public static void Apply(ClientWebSocketOptions options)
    {
        options.SetRequestHeader(VersionHeader, Version);
        options.SetRequestHeader(PlatformHeader, Platform);
    }
}

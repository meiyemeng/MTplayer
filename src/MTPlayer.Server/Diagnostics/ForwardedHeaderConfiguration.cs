using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace MTPlayer.Server.Diagnostics;

public static class ForwardedHeaderConfiguration
{
    public static void Apply(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedHost |
            ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = Math.Clamp(
            configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1,
            1,
            5);

        foreach (var value in configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        {
            if (IPAddress.TryParse(value, out var address))
            {
                options.KnownProxies.Add(address);
            }
        }

        foreach (var value in configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
        {
            if (TryParseNetwork(value, out var network))
            {
                options.KnownNetworks.Add(network);
            }
        }
    }

    internal static bool TryParseNetwork(
        string? value,
        out Microsoft.AspNetCore.HttpOverrides.IPNetwork network)
    {
        network = default!;
        var parts = value?.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 } ||
            !IPAddress.TryParse(parts[0], out var prefix) ||
            !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var maximum = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength is < 0 || prefixLength > maximum)
        {
            return false;
        }

        network = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength);
        return true;
    }
}

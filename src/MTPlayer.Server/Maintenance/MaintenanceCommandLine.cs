namespace MTPlayer.Server.Maintenance;

public sealed record MaintenanceCommandLine(
    string? ExportOpenApiPath,
    bool RotateKey,
    string? NewEncryptionKey)
{
    public static MaintenanceCommandLine Parse(string[] args)
    {
        string? exportPath = null;
        string? newKey = null;
        var rotate = args.Any(argument => string.Equals(argument, "rotate-key", StringComparison.Ordinal));
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--export-openapi", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                exportPath = args[++index];
            }
            else if (string.Equals(args[index], "--new-key", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                newKey = args[++index];
            }
        }

        if (exportPath is not null && rotate)
        {
            throw new ArgumentException("OpenAPI export and key rotation cannot run together.", nameof(args));
        }

        if (newKey is not null && !rotate)
        {
            throw new ArgumentException("--new-key can only be used with rotate-key.", nameof(args));
        }

        return new MaintenanceCommandLine(exportPath, rotate, newKey);
    }
}

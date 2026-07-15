using Microsoft.EntityFrameworkCore;
using MTPlayer.Server.Data;

namespace MTPlayer.Server.Diagnostics;

public static class DatabaseStartup
{
    public static async Task ApplyMigrationsAsync(
        IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Database:MigrateOnStartup", false))
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}

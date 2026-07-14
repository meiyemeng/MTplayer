using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MTPlayer.Server.Data;

public sealed class ApiDbContextFactory : IDesignTimeDbContextFactory<ApiDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=127.0.0.1;Port=1;Database=mtplayer_design_time_only;" +
        "Username=mtplayer_design_time_only;Timeout=1;Command Timeout=1;Pooling=false";

    public ApiDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApiDbContext>()
            .UseNpgsql(DesignTimeConnectionString)
            .Options;

        return new ApiDbContext(options);
    }
}

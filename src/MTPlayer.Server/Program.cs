using Microsoft.EntityFrameworkCore;
using MTPlayer.Server.Data;

var builder = WebApplication.CreateBuilder(args);
const string postgreSqlConnectionStringKey = "ConnectionStrings:PostgreSQL";
var postgreSqlConnectionString = builder.Configuration[postgreSqlConnectionStringKey];
if (string.IsNullOrWhiteSpace(postgreSqlConnectionString))
{
    throw new InvalidOperationException(
        $"Configuration value '{postgreSqlConnectionStringKey}' is required and cannot be empty.");
}

builder.Services.AddDbContext<ApiDbContext>(options =>
    options.UseNpgsql(postgreSqlConnectionString));

var app = builder.Build();

app.Run();

public partial class Program
{
}

using Microsoft.EntityFrameworkCore;
using MTPlayer.Server.Data;
using MTPlayer.Server.Security;

var builder = WebApplication.CreateBuilder(args);
const string postgreSqlConnectionStringKey = "ConnectionStrings:PostgreSQL";
const string dataEncryptionKeyConfigurationKey = "DATA_ENCRYPTION_KEY";
var dataEncryptionKey = builder.Configuration[dataEncryptionKeyConfigurationKey] ?? string.Empty;
try
{
    AesGcmSecretProtector.ValidateKey(dataEncryptionKey);
}
catch (ArgumentException exception)
{
    throw new InvalidOperationException(
        "DATA_ENCRYPTION_KEY must be a Base64 encoded 32-byte key.",
        exception);
}

var postgreSqlConnectionString = builder.Configuration[postgreSqlConnectionStringKey];
if (string.IsNullOrWhiteSpace(postgreSqlConnectionString))
{
    throw new InvalidOperationException(
        $"Configuration value '{postgreSqlConnectionStringKey}' is required and cannot be empty.");
}

builder.Services.AddDbContext<ApiDbContext>(options =>
    options.UseNpgsql(postgreSqlConnectionString));
builder.Services.AddSingleton<ISecretProtector>(
    _ => new AesGcmSecretProtector(dataEncryptionKey));
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<TokenFactory>();

var app = builder.Build();
_ = app.Services.GetRequiredService<ISecretProtector>();

app.Run();

public partial class Program
{
}

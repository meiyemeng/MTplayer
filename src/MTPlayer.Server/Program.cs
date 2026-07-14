using Microsoft.EntityFrameworkCore;
using MTPlayer.Server.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ApiDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")));

var app = builder.Build();

app.Run();

public partial class Program
{
}

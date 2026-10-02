using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SeatFlow.Data;
using SeatFlow.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddDbContext<SeatFlowDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("SeatFlow")));
builder.Services.AddScoped<ExpiredHoldCleanup>();
var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? "localhost:6379,abortConnect=false";
builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(redisConnectionString));
builder.Services.AddSingleton<RedisSeatLockService>();
builder.Services.AddHostedService<ExpiredHoldCleanupWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

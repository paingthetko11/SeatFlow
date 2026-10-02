using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SeatFlow.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddDbContext<SeatFlowDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("SeatFlow")));

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

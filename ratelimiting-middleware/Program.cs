using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddRateLimiter(options =>
{
    //There are a few Limiter types to implement your logic
    //1- Concurrency
    //2- FixedWindowsLimit
    //3- SlidingWindowsLimit
    //4- TokenBucketLimit

    //protect us from DOS Attacks and our resources. 
    //this middleware is part of System.Threading.RateLimiting



    //with GlobalLimiter
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.User.Identity?.Name ?? context.Request.Headers.Host.ToString(),
        factory: partition => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 10,
            QueueLimit = 0,

            //you dont need to reject directly, if you want you can ship the request to a queue and consume. 
            //QueueLimit = 6,
            //QueueProcessingOrder = QueueProcessingOrder.OldestFirst,

            Window = TimeSpan.FromMinutes(1)

        }));

    //with Policies
    options.AddFixedWindowLimiter("Level1", options =>
    {
        options.AutoReplenishment = true;
        options.PermitLimit = 10;
        options.Window = TimeSpan.FromMinutes(1);
    });

    options.AddFixedWindowLimiter("Level2ForController", options =>
    {
        options.AutoReplenishment = true;
        options.PermitLimit = 10;
        options.Window = TimeSpan.FromMinutes(1);
    });

    options.AddConcurrencyLimiter("C1", opt =>
    {
        opt.PermitLimit = 100;
        opt.QueueLimit = 1;
        opt.QueueProcessingOrder = QueueProcessingOrder.NewestFirst;
    })
    .AddPolicy("C1.1", httpContext =>
    {

        string userName = httpContext.User.Identity?.Name ?? string.Empty;
        if (!StringValues.IsNullOrEmpty(userName))
        {
            return RateLimitPartition.GetTokenBucketLimiter(userName, _ =>
                new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 6000,
                    QueueLimit = 60,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(100),
                    TokensPerPeriod = 100,
                    AutoReplenishment = true
                });
        }
        else
        {
            return RateLimitPartition.GetTokenBucketLimiter("AnonymousUser", _ =>
               new TokenBucketRateLimiterOptions
               {
                   TokenLimit = 100,
                   QueueLimit = 5,
                   ReplenishmentPeriod = TimeSpan.FromSeconds(500),
                   TokensPerPeriod = 50,
                   AutoReplenishment = true
               });
        }

    });

    options.AddSlidingWindowLimiter("Sliding1", options =>
    {
        options.PermitLimit = 100;
        options.Window = TimeSpan.FromSeconds(100);
        options.SegmentsPerWindow = 50;
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        options.QueueLimit = 10;
    });

    options.AddTokenBucketLimiter("Token1", options =>
    {
        options.TokenLimit = 500;
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        options.QueueLimit = 50;
        options.ReplenishmentPeriod = TimeSpan.FromSeconds(100);
        options.TokensPerPeriod = 200;
        options.AutoReplenishment = true;
    });

    //What will happen if we reach to limits...
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.RequestServices.GetService<ILoggerFactory>()?
           .CreateLogger("Microsoft.AspNetCore.RateLimitingMiddleware")
           .LogWarning("OnRejected: {GetUserEndPoint}", GetUserEndPoint(context.HttpContext));

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            await context.HttpContext.Response.WriteAsync($"Too many requests. Please try again after {retryAfter.TotalMinutes} minute(s). For details https://.", cancellationToken: token);
        }
        else
        {
            await context.HttpContext.Response.WriteAsync("Rate limit exceeded.", cancellationToken: token);
        }


    };
});


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();

static string GetUserEndPoint(HttpContext context) =>
   $"User {context.User.Identity?.Name ?? "Anonymous"} endpoint:{context.Request.Path}"
   + $" {context.Connection.RemoteIpAddress}";

app.MapGet("/api/hello", () => "Hello RateLimiter").RequireRateLimiting("Level1");

app.Run();




using Microsoft.AspNetCore.RateLimiting;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", opt =>
    {
        opt.PermitLimit = 10;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
    options.RejectionStatusCode = 429;
});

builder.Services.AddHealthChecks();

var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "gateway-.log");

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console()
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14));

var app = builder.Build();

// 生产才跳 HTTPS，本机 HTTP 测试不受影响
if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.MapHealthChecks("/health").AllowAnonymous();

// ① 先重写 "/" → "/index.html"
app.UseDefaultFiles();

// ② 再处理静态文件（只调用这一次！）
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        var path = ctx.Context.Request.Path.Value ?? "";
        headers.CacheControl = path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
    }
});

app.UseRateLimiter();
app.MapReverseProxy();

// ③ fallback 要传 noCache
var noCache = new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "no-cache"
};
app.MapFallbackToFile("index.html", noCache);

app.Run();
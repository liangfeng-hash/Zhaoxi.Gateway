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

// 部署验证端点：返回程序集的构建时间和主机名。
// 每次发布后对比 buildTime 是否变新，就能确认「跑的确实是这次构建的产物」，
// 而不是 robocopy 没同步上、或应用池没回收导致的旧进程。
app.MapGet("/gateway/info", () =>
{
    var asm = System.Reflection.Assembly.GetEntryAssembly()!;
    return Results.Json(new
    {
        service    = "Zhaoxi.Gateway",
        version    = asm.GetName().Version?.ToString(),
        machine    = Environment.MachineName,
        buildTime  = System.IO.File.GetLastWriteTime(asm.Location).ToString("yyyy-MM-dd HH:mm:ss"),
        serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
    });
}).AllowAnonymous();

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
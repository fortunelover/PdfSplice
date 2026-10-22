using PdfSplice.Core;
using PdfSplice.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ===== PDF 文件端点：文件上传/下载走普通 HTTP multipart，不经 SignalR（避开 32KB 单消息上限）。
// 手动 ReadFormAsync 读表单（不走参数绑定），不触发 antiforgery 自动校验，前端裸 fetch 即可。
const string PdfContentType = "application/pdf";

// PDF 合并：多文件 -> 一个 PDF
app.MapPost("/api/pdf/combine", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
    var streams = new List<Stream>();
    try
    {
        foreach (var file in form.Files)
        {
            var ms = new MemoryStream();
            await file.CopyToAsync(ms, ctx.RequestAborted);
            ms.Position = 0;
            streams.Add(ms);
        }
        if (streams.Count == 0) return Results.BadRequest("未上传任何 PDF 文件");
        var bytes = PdfTools.Combine(streams);
        return Results.File(bytes, PdfContentType, "combined.pdf");
    }
    finally
    {
        foreach (var s in streams) s.Dispose();
    }
});

// PDF 拆分：一个 PDF -> zip，内含每页 Page-1.pdf、Page-2.pdf ...
app.MapPost("/api/pdf/split", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
    var file = form.Files[0];
    if (file is null) return Results.BadRequest("未上传任何 PDF 文件");
    await using var src = file.OpenReadStream();
    var pages = PdfTools.Split(src);

    var zip = new MemoryStream();
    using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        for (int i = 0; i < pages.Count; i++)
        {
            var entry = archive.CreateEntry($"Page-{i + 1}.pdf", System.IO.Compression.CompressionLevel.Optimal);
            await using var es = entry.Open();
            await es.WriteAsync(pages[i], ctx.RequestAborted);
        }
    }
    zip.Position = 0;
    return Results.File(zip.ToArray(), "application/zip", "split-pages.zip");
});

// PDF 拼版（N 合一）：多文件 + 表单参数 top/bottom/left/right/maxRow -> 一个 PDF
app.MapPost("/api/pdf/splice", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
    var options = new PdfSpliceOptions
    {
        Top = ParseDouble(form, "top"),
        Bottom = ParseDouble(form, "bottom"),
        Left = ParseDouble(form, "left"),
        Right = ParseDouble(form, "right"),
        MaxRow = (int)ParseDouble(form, "maxRow", 2),
    };
    var streams = new List<Stream>();
    try
    {
        foreach (var file in form.Files)
        {
            var ms = new MemoryStream();
            await file.CopyToAsync(ms, ctx.RequestAborted);
            ms.Position = 0;
            streams.Add(ms);
        }
        if (streams.Count == 0) return Results.BadRequest("未上传任何 PDF 文件");
        var bytes = PdfTools.Splice(streams, options);
        return Results.File(bytes, PdfContentType, "spliced.pdf");
    }
    finally
    {
        foreach (var s in streams) s.Dispose();
    }
});

app.Run();

static double ParseDouble(IFormCollection form, string key, double defaultValue = 0d)
    => double.TryParse(form[key], out var v) ? v : defaultValue;

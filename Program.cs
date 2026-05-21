using ChatClient.Models;
using ChatClient.Services;

var builder = WebApplication.CreateBuilder(args);

var config = builder.Configuration.GetSection("ChatSettings");
var serverUrl = config["ServerUrl"]!;
var lmUrl = config["LmStudioUrl"]!;
var login = config["Login"]!;
var password = config["Password"]!;
var chatId = ulong.Parse(config["ChatId"]!);
var mockMode = bool.Parse(config["MockMode"] ?? "false");

var tokenService = new TokenService();
var lmService = new LmStudioService(lmUrl);
var signalR = new SignalRService(serverUrl, login, password, chatId, tokenService, lmService, mockMode);

builder.Services.AddSingleton(tokenService);
builder.Services.AddSingleton(lmService);
builder.Services.AddSingleton(signalR);

var app = builder.Build();
app.UseStaticFiles();

app.MapGet("/api/history", async (SignalRService sr) =>
{
    await sr.RefreshHistoryAsync();
    return Results.Ok(sr.ChatHistory);
});

app.MapPost("/api/send-anonymous", async (Message msg, SignalRService sr) =>
{
    await sr.SendAnonymousMessageAsync(msg.Text);
    return Results.Ok();
});

app.MapGet("/api/status", (SignalRService sr) =>
    Results.Ok(new { status = sr.GetStatus() }));

app.MapFallbackToFile("index.html");

var signalRInstance = app.Services.GetRequiredService<SignalRService>();
_ = Task.Run(async () =>
{
    try
    {
        await signalRInstance.InitializeAsync();
        Console.WriteLine("[App] Клиент успешно запущен");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[App] Ошибка запуска: {ex.Message}");
    }
});

app.Run("http://0.0.0.0:5001");
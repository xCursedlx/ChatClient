using Microsoft.AspNetCore.SignalR.Client;
using ChatClient.Models;
using System.Text.Json;

namespace ChatClient.Services;

public class SignalRService
{
    private HubConnection? _connection;
    private readonly string _serverUrl;
    private readonly string _login;
    private readonly string _password;
    private readonly ulong _chatId;
    private readonly TokenService _tokenService;
    private readonly LmStudioService _lmService;
    private readonly bool _mockMode;
    private volatile bool _isGenerating = false;

    public List<Message> ChatHistory { get; private set; } = new();
    public string Status { get; private set; } = "Отключено";

    public SignalRService(string serverUrl, string login, string password,
        ulong chatId, TokenService tokenService, LmStudioService lmService,
        bool mockMode = false)
    {
        _serverUrl = serverUrl;
        _login = login;
        _password = password;
        _chatId = chatId;
        _tokenService = tokenService;
        _lmService = lmService;
        _mockMode = mockMode;
    }

    public async Task InitializeAsync()
    {
        if (_mockMode)
        {
            await InitializeMockAsync();
            return;
        }

        Status = "Подключение...";
        Console.WriteLine("[SignalR] Первое подключение для авторизации...");

        _connection = new HubConnectionBuilder()
            .WithUrl(_serverUrl)
            .Build();

        await _connection.StartAsync();
        Console.WriteLine("[SignalR] Подключено анонимно");

        try
        {
            var registerResult = await _connection.InvokeAsync<Response>("Register", _login, _password);
            Console.WriteLine($"[Auth] Регистрация: {registerResult.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] Регистрация: {ex.Message}");
        }

        var authResult = await _connection.InvokeAsync<Response>("Authorize", _login, _password);
        Console.WriteLine($"[Auth] Авторизация: {authResult.Message}");

        if (authResult.StatusCode == 200 && authResult.Data != null)
        {
            var token = JsonSerializer.Deserialize<string>(authResult.Data)!;
            _tokenService.SaveToken(token);
            Console.WriteLine("[Auth] Токен получен");
        }
        else
        {
            throw new Exception($"Авторизация не удалась: {authResult.Message}");
        }

        await _connection.DisposeAsync();
        await ConnectWithTokenAsync();
    }

    private async Task InitializeMockAsync()
    {
        Console.WriteLine("[MOCK] Запуск в мок-режиме, сервер не нужен");
        Status = "Мок-режим";

        ChatHistory = new List<Message>
        {
            new() { Id = 1, UserLogin = "bot_alice", Content = "Всем привет, как дела?", Timestamp = DateTime.Now.AddMinutes(-10), ChatId = 1 },
            new() { Id = 2, UserLogin = "anonymous", Content = "Нормально, тихо пока", Timestamp = DateTime.Now.AddMinutes(-8), ChatId = 1 },
            new() { Id = 3, UserLogin = "bot_boris", Content = "Я тут, готов к диалогу", Timestamp = DateTime.Now.AddMinutes(-6), ChatId = 1 },
            new() { Id = 4, UserLogin = "jojik_client", Content = "Братан, у тебя есть пиво? Реально умираю от жажды", Timestamp = DateTime.Now.AddMinutes(-4), ChatId = 1 },
            new() { Id = 5, UserLogin = "bot_alice", Content = "Какое пиво? Мы в чате", Timestamp = DateTime.Now.AddMinutes(-2), ChatId = 1 },
        };

        _ = GenerateAndAddReplyAsync(5000);

        await Task.CompletedTask;
    }

    private async Task GenerateAndAddReplyAsync(int delayMs = 2000)
    {
        if (_isGenerating) return;
        _isGenerating = true;

        try
        {
            await Task.Delay(delayMs);
            var historySnapshot = ChatHistory.ToList();
            Console.WriteLine($"[MOCK] Генерирую ответ на {historySnapshot.Count} сообщений...");

            var reply = await _lmService.GenerateReplyAsync(historySnapshot);
            Console.WriteLine($"[MOCK] Нейронка ответила: {reply}");

            ChatHistory.Add(new Message
            {
                Id = (ulong)(ChatHistory.Count + 1),
                UserLogin = _login,
                Content = reply,
                Timestamp = DateTime.Now,
                ChatId = 1
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MOCK] Ошибка нейронки: {ex.Message}");
            ChatHistory.Add(new Message
            {
                Id = (ulong)(ChatHistory.Count + 1),
                UserLogin = _login,
                Content = "[LM Studio недоступен — проверь что модель запущена]",
                Timestamp = DateTime.Now,
                ChatId = 1
            });
        }
        finally
        {
            _isGenerating = false;
        }
    }

    private async Task ConnectWithTokenAsync()
    {
        Console.WriteLine("[SignalR] Подключение с токеном...");

        _connection = new HubConnectionBuilder()
            .WithUrl(_serverUrl, options =>
            {
                options.AccessTokenProvider = () =>
                    Task.FromResult<string?>(_tokenService.GetToken());
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.Reconnecting += _ =>
        {
            Status = "Переподключение...";
            Console.WriteLine("[SignalR] Переподключение...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += _ =>
        {
            Status = "Подключено";
            Console.WriteLine("[SignalR] Переподключено успешно");
            return Task.CompletedTask;
        };

        _connection.Closed += _ =>
        {
            Status = "Отключено";
            Console.WriteLine("[SignalR] Соединение закрыто");
            return Task.CompletedTask;
        };

        _connection.On<List<Message>>("GenerateResponse", async (messages) =>
        {
            Console.WriteLine($"[AI] Получено {messages.Count} сообщений, генерирую ответ...");
            ChatHistory = messages.OrderBy(m => m.Timestamp).ToList();

            try
            {
                var reply = await _lmService.GenerateReplyAsync(messages);
                Console.WriteLine($"[AI] Ответ: {reply}");
                await SendAiMessageAsync(reply);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Ошибка генерации: {ex.Message}");
            }
        });

        await _connection.StartAsync();
        Status = "Подключено";
        Console.WriteLine("[SignalR] Подключено с токеном");

        _ = StartTokenRefreshLoopAsync();
    }

    private async Task StartTokenRefreshLoopAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(18));
            if (_tokenService.IsExpired())
            {
                Console.WriteLine("[Auth] Токен истекает, переавторизация...");
                await InitializeAsync();
            }
        }
    }

    public async Task SendAiMessageAsync(string content)
    {
        if (_connection is null) return;
        await _connection.InvokeAsync("SendMessageAi", content, _chatId);
    }

    public async Task SendAnonymousMessageAsync(string content)
    {
        if (_mockMode)
        {
            ChatHistory.Add(new Message
            {
                Id = (ulong)(ChatHistory.Count + 1),
                UserLogin = "anonymous",
                Content = content,
                Timestamp = DateTime.Now,
                ChatId = 1
            });

            _ = GenerateAndAddReplyAsync(2000);
            return;
        }

        if (_connection is null) return;
        await _connection.InvokeAsync("SendMessage", content, _chatId);
    }

    public async Task RefreshHistoryAsync()
    {
        if (_mockMode) return;
        if (_connection is null) return;

        try
        {
            var result = await _connection.InvokeAsync<Response>("GetMessages", _chatId);
            if (result.Data != null)
            {
                ChatHistory = (JsonSerializer.Deserialize<List<Message>>(
                    result.Data,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? new()).OrderBy(m => m.Timestamp).ToList();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SignalR] Ошибка получения истории: {ex.Message}");
        }
    }

    public string GetStatus() => Status;
}
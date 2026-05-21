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
    private ulong _chatId;
    private readonly TokenService _tokenService;
    private readonly LmStudioService _lmService;
    private readonly bool _mockMode;
    private readonly HttpClient _restClient;
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

        var uri = new Uri(serverUrl);
        var baseUrl = $"{uri.Scheme}://{uri.Authority}";
        _restClient = new HttpClient(CreateHttpHandler())
        {
            BaseAddress = new Uri(baseUrl)
        };
    }

    private HttpClientHandler CreateHttpHandler()
    {
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
    }

    public async Task InitializeAsync()
    {
        if (_mockMode)
        {
            await InitializeMockAsync();
            return;
        }

        await AuthorizeClientAsync(_login, _password, _tokenService);
        await ConnectWithTokenAsync();
    }

    private async Task AuthorizeClientAsync(string login, string password, TokenService tokenService)
    {
        Console.WriteLine($"[Auth] Авторизация {login} через REST...");

        try
        {
            var regResponse = await _restClient.PostAsync(
                $"/api/Auth/Register?login={login}&password={password}", null);
            var regJson = await regResponse.Content.ReadAsStringAsync();
            using var regDoc = JsonDocument.Parse(regJson);
            var regMessage = regDoc.RootElement.GetProperty("message").GetString();
            Console.WriteLine($"[Auth] Регистрация {login}: {regMessage}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] Регистрация {login}: {ex.Message}");
        }

        var authResponse = await _restClient.PostAsync(
            $"/api/Auth/Login?login={login}&password={password}", null);
        var authJson = await authResponse.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(authJson);
        var root = doc.RootElement;
        var statusCode = root.GetProperty("statusCode").GetInt32();

        if (statusCode == 200)
        {
            var data = root.GetProperty("data").GetString();
            var token = data?.Trim('"') ?? "";
            tokenService.SaveToken(token);
            Console.WriteLine($"[Auth] Токен {login} получен");
        }
        else
        {
            var message = root.GetProperty("message").GetString();
            throw new Exception($"Авторизация {login} не удалась: {message}");
        }
    }

    private async Task ConnectWithTokenAsync()
    {
        Console.WriteLine("[SignalR] Подключение с токеном...");

        _connection = new HubConnectionBuilder()
            .WithUrl(_serverUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => CreateHttpHandler();
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
                Console.WriteLine("[AI] Убедись что LM Studio запущен на http://localhost:1234");
            }
        });

        await _connection.StartAsync();
        Console.WriteLine("[SignalR] Подключено с токеном");

        if (_chatId == 0)
        {
            Console.WriteLine("[Chat] ChatId не задан, ищем существующий чат...");
            var chatsResult = await _connection.InvokeAsync<Response>("GetChats");
            Console.WriteLine($"[Chat] GetChats: {chatsResult.Message}");
            var dataStr = chatsResult.Data?.ToString() ?? "";

            if (!string.IsNullOrEmpty(dataStr) && dataStr != "[]" && dataStr != "null")
            {
                using var doc = JsonDocument.Parse(dataStr);
                _chatId = doc.RootElement[0].GetProperty("Id").GetUInt64();
                Console.WriteLine($"[Chat] Нашли существующий чат ID: {_chatId}");
            }
            else
            {
                Console.WriteLine("[Chat] Чатов нет, создаём новый...");
                var createResult = await _connection.InvokeAsync<Response>(
                    "CreateChat", "AI Chat", Array.Empty<string>());
                Console.WriteLine($"[Chat] Создание чата: {createResult.Message}");

                var chatsResult2 = await _connection.InvokeAsync<Response>("GetChats");
                var dataStr2 = chatsResult2.Data?.ToString() ?? "";
                Console.WriteLine($"[Chat] Chat created - Data: {dataStr2}");

                if (!string.IsNullOrEmpty(dataStr2) && dataStr2 != "[]")
                {
                    using var doc2 = JsonDocument.Parse(dataStr2);
                    _chatId = doc2.RootElement[0].GetProperty("Id").GetUInt64();
                    Console.WriteLine($"[Chat] Чат создан, ID: {_chatId}");
                }
            }
        }
        else
        {
            Console.WriteLine($"[Chat] Используем ChatId: {_chatId}");
        }

        Status = "Подключено";
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
                await AuthorizeClientAsync(_login, _password, _tokenService);
            }
        }
    }

    private async Task InitializeMockAsync()
    {
        Console.WriteLine("[MOCK] Запуск в мок-режиме, сервер не нужен");
        Status = "Мок-режим";

        ChatHistory = new List<Message>
        {
            new() { Id = Guid.NewGuid(), SenderLogin = "bot_alice", Text = "Всем привет, как дела?", Timestamp = DateTimeOffset.Now.AddMinutes(-10), ChatId = 1 },
            new() { Id = Guid.NewGuid(), SenderLogin = "anonymous", Text = "Нормально, тихо пока", Timestamp = DateTimeOffset.Now.AddMinutes(-8), ChatId = 1 },
            new() { Id = Guid.NewGuid(), SenderLogin = "bot_boris", Text = "Я тут, готов к диалогу", Timestamp = DateTimeOffset.Now.AddMinutes(-6), ChatId = 1 },
            new() { Id = Guid.NewGuid(), SenderLogin = "jojik_client", Text = "Братан, у тебя есть пиво? Реально умираю от жажды", Timestamp = DateTimeOffset.Now.AddMinutes(-4), ChatId = 1 },
            new() { Id = Guid.NewGuid(), SenderLogin = "bot_alice", Text = "Какое пиво? Мы в чате", Timestamp = DateTimeOffset.Now.AddMinutes(-2), ChatId = 1 },
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
                Id = Guid.NewGuid(),
                SenderLogin = _login,
                Text = reply,
                Timestamp = DateTimeOffset.Now,
                ChatId = 1
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MOCK] Ошибка нейронки: {ex.Message}");
            ChatHistory.Add(new Message
            {
                Id = Guid.NewGuid(),
                SenderLogin = _login,
                Text = "[LM Studio недоступен — проверь что модель запущена]",
                Timestamp = DateTimeOffset.Now,
                ChatId = 1
            });
        }
        finally
        {
            _isGenerating = false;
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
                Id = Guid.NewGuid(),
                SenderLogin = "anonymous",
                Text = content,
                Timestamp = DateTimeOffset.Now,
                ChatId = 1
            });

            _ = GenerateAndAddReplyAsync(2000);
            return;
        }

        if (_connection is null) return;
        await _connection.InvokeAsync("SendMessage", $"[Аноним]: {content}", _chatId);
    }

    public async Task RefreshHistoryAsync()
    {
        if (_mockMode) return;
        if (_connection is null) return;

        try
        {
            var result = await _connection.InvokeAsync<Response>("GetMessages", _chatId);
            var dataStr = result.Data?.ToString() ?? "";
            if (!string.IsNullOrEmpty(dataStr))
            {
                ChatHistory = (JsonSerializer.Deserialize<List<Message>>(
                    dataStr,
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
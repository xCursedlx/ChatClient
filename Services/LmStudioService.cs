using System.Text;
using System.Text.Json;
using ChatClient.Models;

namespace ChatClient.Services;

public class LmStudioService
{
    private readonly HttpClient _http;

    public LmStudioService(string apiUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(apiUrl) };
    }

    public async Task<string> GenerateReplyAsync(List<Message> history)
    {
        if (history.Count == 0)
            return "";

        var messages = new List<object>();

        foreach (var msg in history)
        {
            var isLast = msg == history.Last();
            messages.Add(new
            {
                role = isLast ? "user" : "assistant",
                content = $"{msg.UserLogin}: {msg.Content}"
            });
        }

        var requestBody = new
        {
            model = "local-model",
            messages = messages,
            temperature = 0.9,
            max_tokens = 300
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("/v1/chat/completions", content);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);

        var reply = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        var colonIndex = reply.IndexOf(':');
        if (colonIndex > 0 && colonIndex < 40)
        {
            var prefix = reply[..colonIndex].Trim();
            if (!prefix.Contains('.') && !prefix.Contains('!') && !prefix.Contains('?'))
                reply = reply[(colonIndex + 1)..].TrimStart();
        }

        return reply;
    }
}
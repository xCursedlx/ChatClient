using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        var lastMessage = history.First();
        var chatContext = string.Join("\n",
            history.AsEnumerable().Reverse()
                .Select(m => $"{m.SenderLogin}: {m.Text}"));

        var messages = new List<object>
        {
            new
            {
                role = "user",
                content = $"История чата:\n{chatContext}\n\nПоследнее сообщение от {lastMessage.SenderLogin}: \"{lastMessage.Text}\"\n\nОтветь именно на это последнее сообщение."
            }
        };

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

        reply = Regex.Replace(reply, @"\b\w+_\w+\s*:\s*", "");
        reply = Regex.Replace(reply, @"^\w+\s*:\s*", "");

        return reply.Trim();
    }
}
namespace ChatClient.Models;

public class Message
{
    public ulong Id { get; set; }
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public ulong ChatId { get; set; }
    public string UserLogin { get; set; } = "";
}
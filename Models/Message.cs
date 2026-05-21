namespace ChatClient.Models;

public class Message
{
    public Guid Id { get; set; }
    public string Text { get; set; } = "";
    public string SenderLogin { get; set; } = "";
    public ulong ChatId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
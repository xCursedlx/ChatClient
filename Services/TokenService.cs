namespace ChatClient.Services;

public class TokenService
{
    private string? _token;
    private DateTime _expiresAt;

    public void SaveToken(string token)
    {
        _token = token;
        _expiresAt = DateTime.UtcNow.AddMinutes(18);
    }

    public string? GetToken() => _token;

    public bool IsExpired() => _token is null || DateTime.UtcNow >= _expiresAt;
}
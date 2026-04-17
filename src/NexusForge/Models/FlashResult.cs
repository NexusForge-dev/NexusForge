namespace NexusForge.Models;

public class FlashResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public bool Verified { get; set; }
}

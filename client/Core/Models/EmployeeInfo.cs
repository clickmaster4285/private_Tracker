namespace client.Core.Models;

public class EmployeeInfo
{
    public string Id { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Shift { get; set; } = string.Empty;
    public string? Avatar { get; set; }
    public string? AvatarColor { get; set; }
    public string? Token { get; set; }
    public string? DeviceToken { get; set; }
    public string? LoggedInAt { get; set; }
}

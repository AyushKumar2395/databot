using Microsoft.AspNetCore.Identity;

namespace Domain.Entities;

public sealed class User : IdentityUser
{
    public string DisplayName { get; private set; } = null!;

    private readonly IReadOnlyCollection<UserRole> _roles = [];
    public ICollection<UserRole> UserRoles => [.. _roles];

    public User()
    {
    }

    public User(string displayName, string userName)
    {
        DisplayName = displayName;
        UserName = userName;
        Email = userName;
    }
}
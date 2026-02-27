using Microsoft.AspNetCore.Identity;

namespace Domain.Entities;

public sealed class UserRole : IdentityUserRole<string>
{
    public User User { get; set; } = null!;
    public ApplicationRole Role { get; set; } = null!;
}
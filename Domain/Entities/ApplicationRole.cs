using Microsoft.AspNetCore.Identity;

namespace Domain.Entities;

public sealed class ApplicationRole : IdentityRole
{
    private readonly List<UserRole> _userRoles = [];
    public ICollection<UserRole> UserRoles => _userRoles;

    public ApplicationRole()
    {
    }

    public ApplicationRole(string roleName) : base(roleName)
    {
    }
}
using System;
using System.Collections.Generic;
using BerMap.Domain.Enums;

namespace BerMap.Domain.Entities;

public class User : Entity
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Random value embedded in every access token issued to the user. It is
    /// replaced when the password is reset or changed, the account is disabled
    /// or the role changes, which revokes every token issued before.
    /// </summary>
    public string SecurityStamp { get; set; } = NewSecurityStamp();

    /// <summary>Replaces <see cref="SecurityStamp"/>, revoking the user's outstanding tokens.</summary>
    public void RotateSecurityStamp() => SecurityStamp = NewSecurityStamp();

    private static string NewSecurityStamp() => Guid.NewGuid().ToString("N");

    public ICollection<UserRoleAssignment> Roles { get; set; } = new List<UserRoleAssignment>();
}

public class Role : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public UserRole RoleType { get; set; }

    public ICollection<UserRoleAssignment> Users { get; set; } = new List<UserRoleAssignment>();
}

public class UserRoleAssignment
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
}

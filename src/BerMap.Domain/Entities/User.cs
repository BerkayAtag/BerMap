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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Api.Security;
using BerMap.Application.Interfaces;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BerMap.Api.Controllers;

/// <summary>
/// Local console accounts. Only platform administrators manage users; accounts
/// are deactivated rather than deleted so audit records keep their owner.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.ManageUsers)]
public class UsersController : ControllerBase
{
    private readonly BerMapDbContext _context;
    private readonly IAuditService _auditService;
    private readonly IMemoryCache _cache;

    public UsersController(BerMapDbContext context, IAuditService auditService, IMemoryCache cache)
    {
        _context = context;
        _auditService = auditService;
        _cache = cache;
    }

    public sealed record UserDto(
        Guid Id, string Username, string DisplayName, string Email, string Role,
        bool IsActive, DateTime? LastLogin, DateTime CreatedAt);

    public sealed record CreateUserRequest(string Username, string? DisplayName, string? Email, string Role, string Password);

    public sealed record UpdateUserRequest(string? DisplayName, string? Email, string? Role, bool? IsActive);

    public sealed record ResetPasswordRequest(string Password);

    [HttpGet]
    public async Task<ActionResult<List<UserDto>>> List(CancellationToken cancellationToken)
    {
        var users = await LoadUsers().OrderBy(u => u.Username).ToListAsync(cancellationToken);
        return Ok(users.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        if (username.Length is < 2 or > 64 || username.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return Problem400("invalid_username", "Kullanıcı adı 2-64 karakter olmalı ve boşluk içermemelidir.");

        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: false, out var roleType) || !Enum.IsDefined(roleType))
            return Problem400("invalid_role", "Geçersiz rol.");

        if (PasswordPolicy.Validate(request.Password, username) is { } passwordError)
            return Problem400("weak_password", passwordError);

        if (await _context.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower(), cancellationToken))
            return Conflict(new { message = "Bu kullanıcı adı zaten kullanılıyor.", code = "username_taken" });

        var role = await _context.Roles.FirstAsync(r => r.RoleType == roleType, cancellationToken);
        var user = new User
        {
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim(),
            Email = request.Email?.Trim() ?? string.Empty,
            PasswordHash = PasswordHasher.Hash(request.Password),
            IsActive = true
        };
        user.Roles.Add(new UserRoleAssignment { User = user, RoleId = role.Id });
        _context.Users.Add(user);
        await _context.SaveChangesAsync(cancellationToken);

        await AuditAsync("User.Created", user, new { user.Username, role = roleType.ToString() }, cancellationToken);

        user = await LoadUsers().FirstAsync(u => u.Id == user.Id, cancellationToken);
        return CreatedAtAction(nameof(List), null, ToDto(user));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserDto>> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await LoadUsers(tracking: true).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user == null)
            return NotFound();

        UserRole? newRole = null;
        if (request.Role != null)
        {
            if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed))
                return Problem400("invalid_role", "Geçersiz rol.");
            newRole = parsed;
        }

        var losesAdmin = IsAdmin(user) && user.IsActive &&
                         (request.IsActive == false || (newRole.HasValue && newRole != UserRole.PlatformAdministrator));
        if (losesAdmin && !await AnotherActiveAdminExistsAsync(user.Id, cancellationToken))
            return Conflict(new { message = "Son etkin platform yöneticisi devre dışı bırakılamaz veya rolü değiştirilemez.", code = "last_admin" });

        if (request.IsActive == false && user.Id == CurrentUserId())
            return Conflict(new { message = "Kendi hesabınızı devre dışı bırakamazsınız.", code = "self_deactivation" });

        if (request.DisplayName != null)
            user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? user.Username : request.DisplayName.Trim();
        if (request.Email != null)
            user.Email = request.Email.Trim();
        if (request.IsActive.HasValue)
            user.IsActive = request.IsActive.Value;
        if (newRole.HasValue && PrimaryRole(user) != newRole.Value)
        {
            var role = await _context.Roles.FirstAsync(r => r.RoleType == newRole.Value, cancellationToken);
            user.Roles.Clear();
            user.Roles.Add(new UserRoleAssignment { UserId = user.Id, RoleId = role.Id });
        }
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        // Role and active flag take effect on the user's next request, not when their token expires.
        BearerTokenAuthenticationHandler.InvalidateUserState(_cache, user.Id);
        await AuditAsync("User.Updated", user, new { request.DisplayName, request.Email, request.Role, request.IsActive }, cancellationToken);

        user = await LoadUsers().FirstAsync(u => u.Id == id, cancellationToken);
        return Ok(ToDto(user));
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user == null)
            return NotFound();

        if (PasswordPolicy.Validate(request.Password, user.Username) is { } passwordError)
            return Problem400("weak_password", passwordError);

        user.PasswordHash = PasswordHasher.Hash(request.Password);
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        await AuditAsync("User.PasswordReset", user, new { user.Username }, cancellationToken);
        return NoContent();
    }

    private IQueryable<User> LoadUsers(bool tracking = false)
    {
        var query = _context.Users.Include(u => u.Roles).ThenInclude(r => r.Role);
        return tracking ? query : query.AsNoTracking();
    }

    private async Task<bool> AnotherActiveAdminExistsAsync(Guid exceptUserId, CancellationToken cancellationToken) =>
        await _context.Users.AnyAsync(u => u.Id != exceptUserId && u.IsActive &&
                                          u.Roles.Any(r => r.Role.RoleType == UserRole.PlatformAdministrator),
            cancellationToken);

    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static bool IsAdmin(User user) => user.Roles.Any(r => r.Role.RoleType == UserRole.PlatformAdministrator);

    private static UserRole PrimaryRole(User user) =>
        user.Roles.Select(r => r.Role.RoleType).DefaultIfEmpty(UserRole.ReadOnlyViewer).Min();

    private static UserDto ToDto(User user) => new(
        user.Id, user.Username,
        string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
        user.Email, PrimaryRole(user).ToString(), user.IsActive, user.LastLoginAt, user.CreatedAt);

    private ObjectResult Problem400(string code, string message) =>
        BadRequest(new { message, code });

    private Task AuditAsync(string action, User user, object details, CancellationToken cancellationToken) =>
        _auditService.LogAsync(
            CurrentUserId()?.ToString() ?? string.Empty, this.GetActor(), action, nameof(User), user.Id.ToString(),
            JsonSerializer.Serialize(details), HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            true, cancellationToken);
}

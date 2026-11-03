using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Api.Security;
using BerMap.Application.Interfaces;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace BerMap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    // Verifying against a fixed hash keeps the response time of unknown and
    // known usernames the same, so usernames cannot be enumerated by timing.
    private static readonly string DummyHash = PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    private readonly BerMapDbContext _context;
    private readonly AccessTokenService _tokens;
    private readonly IAuditService _auditService;

    public AuthController(BerMapDbContext context, AccessTokenService tokens, IAuditService auditService)
    {
        _context = context;
        _tokens = tokens;
        _auditService = auditService;
    }

    public record LoginRequest(string Username, string Password);

    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
            return BadRequest(new { message = "Kullanıcı adı ve parola zorunludur.", code = "invalid_credentials" });

        var username = request.Username.Trim();
        var user = await _context.Users
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), cancellationToken);

        var passwordValid = PasswordHasher.Verify(request.Password, user?.PasswordHash ?? DummyHash);
        if (user == null || !user.IsActive || !passwordValid)
        {
            await _auditService.LogAsync(user?.Id.ToString() ?? string.Empty, username, "Auth.LoginFailed",
                "User", user?.Id.ToString() ?? string.Empty, "{\"reason\":\"invalid_credentials\"}",
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty, false, cancellationToken);
            return Unauthorized(new { message = "Kullanıcı adı veya parola hatalı.", code = "invalid_credentials" });
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        var role = PrimaryRole(user);
        var (token, expiresAt) = _tokens.Issue(user.Id, user.Username, user.DisplayName, role);

        await _auditService.LogAsync(user.Id.ToString(), user.Username, "Auth.Login", "User", user.Id.ToString(),
            System.Text.Json.JsonSerializer.Serialize(new { role }), HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty, true, cancellationToken);

        return Ok(new
        {
            token,
            expiresAt,
            user = ToDto(user, role)
        });
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var user = await _context.Users
            .AsNoTracking()
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null || !user.IsActive)
            return Unauthorized(new { message = "Kullanıcı hesabı devre dışı.", code = "account_disabled" });

        return Ok(ToDto(user, PrimaryRole(user)));
    }

    private static string PrimaryRole(User user) =>
        user.Roles
            .Select(r => r.Role.RoleType)
            .DefaultIfEmpty(UserRole.ReadOnlyViewer)
            .Min()
            .ToString();

    private static object ToDto(User user, string role) => new
    {
        id = user.Id,
        username = user.Username,
        displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
        role,
        email = user.Email,
        lastLogin = user.LastLoginAt
    };
}

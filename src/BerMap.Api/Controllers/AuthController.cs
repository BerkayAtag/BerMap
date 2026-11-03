using System;
using Microsoft.AspNetCore.Mvc;

namespace BerMap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    public record LoginRequest(string Username, string Password);

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "Kullanıcı adı ve parola zorunludur.", code = "invalid_credentials" });

        var token = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{request.Username}:{DateTime.UtcNow:O}:{Guid.NewGuid()}"));

        return Ok(new
        {
            token,
            user = new
            {
                id = Guid.NewGuid(),
                username = request.Username.Trim(),
                displayName = request.Username.Trim(),
                role = "PlatformAdministrator",
                email = $"{request.Username.Trim()}@enterprise.local"
            }
        });
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        return Ok(new
        {
            id = Guid.NewGuid(),
            username = "secadmin",
            displayName = "Sistem Yöneticisi",
            role = "PlatformAdministrator",
            email = "secadmin@enterprise.local"
        });
    }
}

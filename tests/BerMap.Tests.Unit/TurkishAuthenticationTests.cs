using System.Globalization;
using BerMap.Api.Controllers;
using BerMap.Api.Security;
using BerMap.Domain.Entities;
using BerMap.Infrastructure.Data;
using BerMap.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BerMap.Tests.Unit;

public class TurkishAuthenticationTests
{
    [Fact]
    public async Task UppercaseAdmin_CanSignInOnTurkishWindows()
    {
        var previous = CultureInfo.CurrentCulture;
        var vault = Path.Combine(Path.GetTempPath(), "bermap-culture-" + Guid.NewGuid().ToString("N"));
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            await using var db = new BerMapDbContext(new DbContextOptionsBuilder<BerMapDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            db.Users.Add(new User { Username = "admin", IsActive = true, PasswordHash = PasswordHasher.Hash("Test-Password-123!") });
            await db.SaveChangesAsync();
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var http = new DefaultHttpContext();
            var controller = new AuthController(db, new AccessTokenService(vault, TimeSpan.FromMinutes(30)),
                new AuditService(db, new HttpContextAccessor { HttpContext = http }), cache)
            { ControllerContext = new ControllerContext { HttpContext = http } };
            var result = await controller.Login(new AuthController.LoginRequest("ADMIN", "Test-Password-123!"), default);
            Assert.IsType<OkObjectResult>(result);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            if (Directory.Exists(vault)) Directory.Delete(vault, true);
        }
    }
}

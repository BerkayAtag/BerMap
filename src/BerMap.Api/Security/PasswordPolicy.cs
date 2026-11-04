using System;

namespace BerMap.Api.Security;

/// <summary>Minimum requirements for local account passwords.</summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    /// <summary>Returns a user-facing (Turkish) reason the password is rejected, or null when it is acceptable.</summary>
    public static string? Validate(string? password, string username)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
            return $"Parola en az {MinimumLength} karakter olmalıdır.";

        if (password.Length > 256)
            return "Parola en fazla 256 karakter olabilir.";

        if (!string.IsNullOrWhiteSpace(username) &&
            password.Contains(username.Trim(), StringComparison.OrdinalIgnoreCase))
            return "Parola kullanıcı adını içeremez.";

        return null;
    }
}

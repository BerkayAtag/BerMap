namespace BerMap.Api.Security;

public static class AuthorizationPolicies
{
    /// <summary>Create, change, delete or run integrations.</summary>
    public const string ManageIntegrations = "ManageIntegrations";

    /// <summary>Change platform state: snapshots, simulations, review decisions.</summary>
    public const string Operate = "Operate";

    /// <summary>Create, change and deactivate console accounts.</summary>
    public const string ManageUsers = "ManageUsers";
}

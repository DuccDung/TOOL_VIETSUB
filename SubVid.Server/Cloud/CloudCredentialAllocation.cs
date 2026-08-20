namespace SubVid.Server.Cloud;

public static class CloudCredentialAllocationModes
{
    public const string Unassigned = "UNASSIGNED";
    public const string Shared = "SHARED";
    public const string Dedicated = "DEDICATED";

    public static bool IsValid(string value) =>
        value is Unassigned or Shared or Dedicated;
}

public static class CloudCredentialAllocationSources
{
    public const string Admin = "ADMIN";
    public const string Plan = "PLAN";
    public const string Migration = "MIGRATION";
}

public static class CloudKeyPoolStatuses
{
    public const string Active = "ACTIVE";
    public const string Disabled = "DISABLED";
}


namespace DysonHarness;

public static class DysonBusScopes
{
    public const string Wildcard = "*";

    public static string Session(Guid persistenceId) => $"session:{persistenceId:D}";

    public static string Subject(string subjectId) => $"subject:{subjectId}";

    public static string Host(Guid hostId) => $"host:{hostId:D}";
}

using System.Reflection;
using Microsoft.Data.SqlClient;

namespace Shop.TestSupport;

/// <summary>Compiled into both test projects: SqlException has no public constructor.</summary>
public static class SqlServerErrors
{
    // SqlException has no public constructor; build one through the provider's internal factory (pinned package version).
    public static SqlException Create(int number, string message = "injected")
    {
        const BindingFlags any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var error = typeof(SqlError).GetConstructor(any, null,
            [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)], null)!
            .Invoke([number, (byte)0, (byte)13, "server", message, "", 1, null]);
        var errors = Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", any)!.Invoke(errors, [error]);
        return (SqlException)typeof(SqlException).GetMethod("CreateException", any, null, [typeof(SqlErrorCollection), typeof(string)], null)!
            .Invoke(null, [errors, "16.0"])!;
    }
}

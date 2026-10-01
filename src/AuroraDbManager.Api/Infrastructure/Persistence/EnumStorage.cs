using System.Text.Json;

namespace AuroraDbManager.Api.Infrastructure.Persistence;

/// <summary>
/// Stores enums as snake_case strings ("provision_instance"), matching how the API serializes them.
/// </summary>
internal static class EnumStorage
{
    public static string ToDbValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static TEnum FromDbValue<TEnum>(string value) where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(value.Replace("_", ""), ignoreCase: true);

    /// <summary>Quoted, comma-separated list of every value, for use in a check constraint.</summary>
    public static string SqlValues<TEnum>() where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetValues<TEnum>().Select(value => $"'{ToDbValue(value)}'"));
}

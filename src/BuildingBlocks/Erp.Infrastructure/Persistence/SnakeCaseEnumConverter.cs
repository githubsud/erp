using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Erp.Infrastructure.Persistence;

/// <summary>Stores enums as readable snake_case text (e.g. <c>SoftClosed</c> → <c>soft_closed</c>) to match SQL CHECK constraints.</summary>
public static class SnakeCaseEnumConverter
{
    private static readonly ConcurrentDictionary<Type, ValueConverter> Cache = new();

    public static ValueConverter Create(Type enumType) =>
        Cache.GetOrAdd(enumType, t => (ValueConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(t))!);

    public static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed class Converter<TEnum>() : ValueConverter<TEnum, string>(v => ToDb(v), v => FromDb(v))
        where TEnum : struct, Enum
    {
        private static readonly Dictionary<TEnum, string> ToText =
            Enum.GetValues<TEnum>().ToDictionary(v => v, v => ToSnakeCase(v.ToString()));

        private static readonly Dictionary<string, TEnum> FromText =
            ToText.ToDictionary(kv => kv.Value, kv => kv.Key);

        private static string ToDb(TEnum value) => ToText[value];

        private static TEnum FromDb(string value) =>
            FromText.TryGetValue(value, out var e) ? e : throw new InvalidOperationException($"Unknown {typeof(TEnum).Name} value '{value}'.");
    }
}

using System.Text.Json;

namespace Relay.Core;

public static class Arguments
{
    /// <summary>
    /// Evaluate arguments.game / arguments.jvm / arguments.default-user-jvm.
    /// Strings are taken directly; objects are evaluated through rules and
    /// their value must be a string or an array of strings.
    /// </summary>
    public static IReadOnlyList<string> Evaluate(JsonElement list, RuleContext context, string path)
    {
        if (list.ValueKind != JsonValueKind.Array)
            throw new RelayException(RelayErrorCode.WrongType, path, "expected an argument array");

        var result = new List<string>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var itemPath = J.Index(path, index++);
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString()!);
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                throw new RelayException(RelayErrorCode.WrongType, itemPath, "argument must be a string or object");

            J.Object(item, itemPath);
            if (!Rules.Evaluate(item, context, itemPath))
                continue;

            var value = J.Required(item, "value", itemPath);
            if (value.ValueKind == JsonValueKind.String)
            {
                result.Add(value.GetString()!);
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array)
                throw new RelayException(RelayErrorCode.WrongType, J.Field(itemPath, "value"), "argument value must be a string or string array");

            var valueIndex = 0;
            foreach (var entry in value.EnumerateArray())
            {
                var entryPath = J.Index(J.Field(itemPath, "value"), valueIndex++);
                if (entry.ValueKind != JsonValueKind.String)
                    throw new RelayException(RelayErrorCode.WrongType, entryPath, "argument value array must contain strings");
                result.Add(entry.GetString()!);
            }
        }

        return result;
    }
}

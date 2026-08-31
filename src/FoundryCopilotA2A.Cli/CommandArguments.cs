namespace FoundryCopilotA2A.Cli;

internal sealed class CommandArguments
{
    private readonly Dictionary<string, string?> _values;

    private CommandArguments(Dictionary<string, string?> values)
    {
        _values = values;
    }

    public static CommandArguments Parse(IEnumerable<string> arguments)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var items = arguments.ToArray();

        for (var index = 0; index < items.Length; index++)
        {
            var argument = items[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
            {
                throw new CliException($"Unexpected argument '{argument}'. Options must start with '--'.");
            }

            var option = argument[2..];
            string? value = null;
            var equalsIndex = option.IndexOf('=');
            if (equalsIndex >= 0)
            {
                value = option[(equalsIndex + 1)..];
                option = option[..equalsIndex];
            }
            else if (index + 1 < items.Length &&
                     !items[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = items[++index];
            }

            if (string.IsNullOrWhiteSpace(option))
            {
                throw new CliException("Option names cannot be empty.");
            }

            if (!values.TryAdd(option, value))
            {
                throw new CliException($"Option '--{option}' was specified more than once.");
            }
        }

        return new CommandArguments(values);
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string Require(string name)
    {
        var value = Optional(name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new CliException($"Option '--{name}' is required.");
    }

    public string? Optional(string name, string? defaultValue = null)
    {
        if (!_values.TryGetValue(name, out var value))
        {
            return defaultValue;
        }

        if (value is null)
        {
            throw new CliException($"Option '--{name}' requires a value.");
        }

        return value;
    }

    public bool Flag(string name)
    {
        if (!_values.TryGetValue(name, out var value))
        {
            return false;
        }

        if (value is null)
        {
            return true;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new CliException($"Option '--{name}' expects true or false.");
    }

    public int Integer(string name, int defaultValue, int minimum = 1)
    {
        var value = Optional(name);
        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(value, out var parsed) && parsed >= minimum
            ? parsed
            : throw new CliException($"Option '--{name}' must be an integer of at least {minimum}.");
    }

    public Uri AbsoluteHttpUri(string name, string? defaultValue = null)
    {
        var value = Optional(name, defaultValue);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new CliException($"Option '--{name}' must be an absolute HTTP or HTTPS URL.");
        }

        return uri;
    }

    public void EnsureOnly(params string[] supported)
    {
        var allowed = supported.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = _values.Keys.FirstOrDefault(option => !allowed.Contains(option));
        if (unknown is not null)
        {
            throw new CliException($"Unknown option '--{unknown}'.");
        }
    }
}

internal sealed class CliException(string message) : Exception(message);

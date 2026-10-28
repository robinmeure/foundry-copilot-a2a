using System.Text;
using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Services.OfficeDocuments;

public sealed record DocumentTable(string[] Columns, string[][] Rows);

public sealed record DocumentSection(string Heading, string[] Paragraphs, string[] Bullets, DocumentTable? Table);

public sealed record WordOutline(string FileName, string Title, string Subtitle, DocumentSection[] Sections);

public sealed record PresentationSlide(string Title, string[] Bullets, string Notes);

public sealed record PresentationOutline(string FileName, string Title, string Subtitle, PresentationSlide[] Slides);

/// <summary>
/// Strict tool schemas and validation for document outlines. The model writes content only;
/// layout, styles and file structure are owned by <see cref="OfficeDocumentRenderer"/>.
/// </summary>
public static class OfficeDocumentOutline
{
    public const string WordToolName = "create_word_document";
    public const string PresentationToolName = "create_presentation";

    public const int MaxTitleLength = 200;
    public const int MaxFileNameLength = 80;
    public const int MaxSections = 30;
    public const int MaxParagraphsPerSection = 20;
    public const int MaxParagraphLength = 4000;
    public const int MaxBulletsPerSection = 30;
    public const int MaxBulletLength = 500;
    public const int MaxTableColumns = 8;
    public const int MaxTableRows = 50;
    public const int MaxCellLength = 300;
    public const int MaxSlides = 30;
    public const int MaxBulletsPerSlide = 8;
    public const int MaxSlideBulletLength = 300;
    public const int MaxNotesLength = 2000;
    public const int MaxTotalCharacters = 60_000;

    /// <summary>Generous enough for a full outline; still bounded so one call cannot exhaust the turn.</summary>
    public const int MaxArgumentLength = 96 * 1024;

    private static object StringArray(string description) =>
        new { type = "array", description, items = new { type = "string" } };

    private static object Text(string description) => new { type = "string", description };

    public static object WordDefinition { get; } = new
    {
        type = "function",
        name = WordToolName,
        description = "Create a Word document (.docx) from an outline you write. The server renders the file and gives the user " +
            "a download link and a Save to OneDrive option. Call it only when the user asks for a document, report, memo or brief.",
        parameters = new
        {
            type = "object",
            properties = new
            {
                fileName = Text($"Short file name without extension, at most {MaxFileNameLength} characters."),
                title = Text($"Document title, at most {MaxTitleLength} characters."),
                subtitle = Text("Subtitle or one-line purpose, or an empty string for none."),
                sections = new
                {
                    type = "array",
                    description = $"1-{MaxSections} sections in reading order.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            heading = Text($"Section heading, at most {MaxTitleLength} characters."),
                            paragraphs = StringArray($"0-{MaxParagraphsPerSection} plain-text paragraphs; **bold** is allowed. No Markdown headings or citation markers."),
                            bullets = StringArray($"0-{MaxBulletsPerSection} short bullet points after the paragraphs."),
                            table = new
                            {
                                type = "object",
                                description = "Optional table after the bullets; use empty arrays for none.",
                                properties = new
                                {
                                    columns = StringArray($"0-{MaxTableColumns} column headers."),
                                    rows = new
                                    {
                                        type = "array",
                                        description = $"0-{MaxTableRows} rows, each with exactly one cell per column.",
                                        items = new { type = "array", items = new { type = "string" } }
                                    }
                                },
                                required = new[] { "columns", "rows" },
                                additionalProperties = false
                            }
                        },
                        required = new[] { "heading", "paragraphs", "bullets", "table" },
                        additionalProperties = false
                    }
                }
            },
            required = new[] { "fileName", "title", "subtitle", "sections" },
            additionalProperties = false
        }
    };

    public static object PresentationDefinition { get; } = new
    {
        type = "function",
        name = PresentationToolName,
        description = "Create a PowerPoint presentation (.pptx) from slides you write. The server adds a title slide, renders the file " +
            "and gives the user a download link and a Save to OneDrive option. Call it only when the user asks for slides, a deck or a presentation.",
        parameters = new
        {
            type = "object",
            properties = new
            {
                fileName = Text($"Short file name without extension, at most {MaxFileNameLength} characters."),
                title = Text($"Presentation title for the title slide, at most {MaxTitleLength} characters."),
                subtitle = Text("Title slide subtitle, such as audience or date, or an empty string for none."),
                slides = new
                {
                    type = "array",
                    description = $"1-{MaxSlides} content slides after the title slide.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = Text($"Slide title, at most {MaxTitleLength} characters."),
                            bullets = StringArray($"1-{MaxBulletsPerSlide} concise bullets, each at most {MaxSlideBulletLength} characters."),
                            notes = Text($"Speaker notes, at most {MaxNotesLength} characters, or an empty string for none.")
                        },
                        required = new[] { "title", "bullets", "notes" },
                        additionalProperties = false
                    }
                }
            },
            required = new[] { "fileName", "title", "subtitle", "slides" },
            additionalProperties = false
        }
    };

    public static WordOutline ParseWord(JsonElement arguments)
    {
        var budget = new Budget();
        RequireObject(arguments, "arguments", "fileName", "title", "subtitle", "sections");
        var sections = Items(arguments, "sections", 1, MaxSections).Select((section, index) =>
        {
            var where = $"sections[{index}]";
            RequireObject(section, where, "heading", "paragraphs", "bullets", "table");
            var table = section.GetProperty("table");
            RequireObject(table, $"{where}.table", "columns", "rows");
            var columns = Strings(table, "columns", 0, MaxTableColumns, MaxCellLength, budget, $"{where}.table");
            var rows = Items(table, "rows", 0, MaxTableRows, $"{where}.table").Select((row, rowIndex) =>
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != columns.Length)
                {
                    throw Invalid($"{where}.table.rows[{rowIndex}] must have exactly one cell per column.");
                }
                return row.EnumerateArray().Select(cell => Line(cell, MaxCellLength, budget, $"{where}.table.rows[{rowIndex}]")).ToArray();
            }).ToArray();
            if (columns.Length == 0 && rows.Length > 0)
            {
                throw Invalid($"{where}.table needs columns when it has rows.");
            }
            return new DocumentSection(
                Line(section.GetProperty("heading"), MaxTitleLength, budget, $"{where}.heading", required: true),
                Strings(section, "paragraphs", 0, MaxParagraphsPerSection, MaxParagraphLength, budget, where, multiline: true),
                Strings(section, "bullets", 0, MaxBulletsPerSection, MaxBulletLength, budget, where),
                columns.Length > 0 ? new DocumentTable(columns, rows) : null);
        }).ToArray();

        return new WordOutline(
            FileName(arguments, budget),
            Line(arguments.GetProperty("title"), MaxTitleLength, budget, "title", required: true),
            Line(arguments.GetProperty("subtitle"), MaxTitleLength, budget, "subtitle"),
            sections);
    }

    public static PresentationOutline ParsePresentation(JsonElement arguments)
    {
        var budget = new Budget();
        RequireObject(arguments, "arguments", "fileName", "title", "subtitle", "slides");
        var slides = Items(arguments, "slides", 1, MaxSlides).Select((slide, index) =>
        {
            var where = $"slides[{index}]";
            RequireObject(slide, where, "title", "bullets", "notes");
            return new PresentationSlide(
                Line(slide.GetProperty("title"), MaxTitleLength, budget, $"{where}.title", required: true),
                Strings(slide, "bullets", 1, MaxBulletsPerSlide, MaxSlideBulletLength, budget, where),
                Clean(ReadString(slide.GetProperty("notes"), $"{where}.notes"), MaxNotesLength, budget, $"{where}.notes", multiline: true));
        }).ToArray();

        return new PresentationOutline(
            FileName(arguments, budget),
            Line(arguments.GetProperty("title"), MaxTitleLength, budget, "title", required: true),
            Line(arguments.GetProperty("subtitle"), MaxTitleLength, budget, "subtitle"),
            slides);
    }

    /// <summary>A safe file name stem: no path, reserved or control characters, and never empty.</summary>
    public static string SafeFileStem(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character) ||
                character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '#' or '%' or '~' or '&' or '{' or '}'
                ? ' '
                : character);
        }
        var stem = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (stem.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) || stem.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^5].Trim('.', ' ');
        }
        if (stem.Length > MaxFileNameLength)
        {
            stem = stem[..MaxFileNameLength].Trim('.', ' ');
        }
        return stem.Length == 0 ? "Document" : stem;
    }

    private sealed class Budget
    {
        public int Characters { get; set; }
    }

    private static string FileName(JsonElement arguments, Budget budget) =>
        SafeFileStem(Line(arguments.GetProperty("fileName"), MaxFileNameLength * 2, budget, "fileName"));

    private static void RequireObject(JsonElement value, string where, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{where} must be an object.");
        }
        var supplied = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (supplied.Length != names.Length || supplied.Distinct(StringComparer.Ordinal).Count() != supplied.Length ||
            !supplied.Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)))
        {
            throw Invalid($"{where} must contain exactly: {string.Join(", ", names)}.");
        }
    }

    private static JsonElement[] Items(JsonElement parent, string name, int minimum, int maximum, string where = "arguments")
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum || value.GetArrayLength() > maximum)
        {
            throw Invalid($"{where}.{name} must be an array of {minimum}-{maximum} items.");
        }
        return value.EnumerateArray().ToArray();
    }

    private static string[] Strings(JsonElement parent, string name, int minimum, int maximum, int maxLength, Budget budget,
        string where, bool multiline = false) =>
        Items(parent, name, minimum, maximum, where)
            .Select((item, index) => Clean(ReadString(item, $"{where}.{name}[{index}]"), maxLength, budget, $"{where}.{name}[{index}]", multiline))
            .Where(item => item.Length > 0)
            .ToArray();

    private static string Line(JsonElement value, int maxLength, Budget budget, string where, bool required = false)
    {
        var text = Clean(ReadString(value, where), maxLength, budget, where, multiline: false);
        return required && text.Length == 0 ? throw Invalid($"{where} must not be empty.") : text;
    }

    private static string ReadString(JsonElement value, string where) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid($"{where} must be a string.");

    /// <summary>Removes characters XML cannot carry and normalizes whitespace; multi-line text keeps single line breaks.</summary>
    private static string Clean(string value, int maxLength, Budget budget, string where, bool multiline)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                builder.Append(character).Append(value[++index]);
            }
            else if (character == '\n' && multiline)
            {
                builder.Append('\n');
            }
            else if (character is '\t' or '\n' or '\r')
            {
                builder.Append(' ');
            }
            else if (!char.IsControl(character) && !char.IsSurrogate(character) && character is not ('\uFFFE' or '\uFFFF'))
            {
                builder.Append(character);
            }
        }
        var lines = builder.ToString().Split('\n')
            .Select(line => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(line => line.Length > 0);
        var text = string.Join('\n', lines);
        if (text.Length > maxLength)
        {
            throw Invalid($"{where} exceeds {maxLength} characters.");
        }
        budget.Characters += text.Length;
        if (budget.Characters > MaxTotalCharacters)
        {
            throw Invalid($"The document exceeds {MaxTotalCharacters:N0} characters. Shorten it or split it into several documents.");
        }
        return text;
    }

    private static ApiException Invalid(string message) => new(400, "invalid_tool_arguments", message);
}

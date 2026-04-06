using System.Buffers;
using System.Text;
using ConsoleAppFramework;
using MessagePack;
using PrettyPrompt;
using PrettyPrompt.Consoles;
using PrettyPrompt.Highlighting;
using Spectre.Console;

namespace DryDB.Cli;

public class Commands
{
    /// <summary>
    /// Open a DryDB database file and start an interactive session or execute a command.
    /// </summary>
    /// <param name="file">Path to the .drydb file</param>
    /// <param name="table">-t, Table name to use (optional, uses first table if not specified)</param>
    /// <param name="command"></param>
    [Command("")]
    public async Task Interactive(string file, string? table = null, [Argument] params string[] command)
    {
        if (!File.Exists(file))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {file}");
            return;
        }

        var fileName = Path.GetFileName(file);

        using var db = await ReadOnlyDatabase.OpenFileAsync(file);

        var tableNames = db.Catalog.TableDescriptors.Keys.ToList();
        if (tableNames.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No tables found in database.");
            return;
        }

        var currentTableName = table ?? tableNames[0];
        if (!db.Catalog.TableDescriptors.ContainsKey(currentTableName))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Table '{currentTableName}' not found.");
            AnsiConsole.MarkupLine($"Available tables: {string.Join(", ", tableNames)}");
            return;
        }

        var currentTable = db.GetTable(currentTableName);

        // One-shot command mode
        if (command.Length > 0)
        {
            await ExecuteCommand(db, currentTable, command[0], command.Skip(1).ToArray());
            return;
        }

        // Interactive mode
        AnsiConsole.MarkupLine($"[green]Connected to[/] [yellow]{fileName}[/]");
        AnsiConsole.MarkupLine($"[dim]Table: {currentTableName} | Type 'help' for commands[/]");
        AnsiConsole.WriteLine();

        var keyBindings = new KeyBindings(
            historyPrevious: new KeyPressPatterns(
                new KeyPressPattern(ConsoleKey.UpArrow),
                new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.P)),
            historyNext: new KeyPressPatterns(
                new KeyPressPattern(ConsoleKey.DownArrow),
                new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.N)));

        await using var prompt = new Prompt(
            configuration: new PromptConfiguration(
                prompt: new FormattedString($"{fileName}({currentTableName})> ",
                    new FormatSpan(0, fileName.Length, AnsiColor.Blue)),
                keyBindings: keyBindings,
                completionItemDescriptionPaneBackground: AnsiColor.Rgb(30, 30, 30),
                selectedCompletionItemBackground: AnsiColor.Rgb(30, 30, 30),
                selectedTextBackground: AnsiColor.Rgb(20, 61, 102)));

        while (true)
        {
            var input = await prompt.ReadLineAsync();
            if (input.CancellationToken.IsCancellationRequested ||
                !input.IsSuccess)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(input.Text))
                continue;

            var parts = ParseCommand(input.Text);
            if (parts.Length == 0)
                continue;

            var cmd = parts[0].ToLowerInvariant();
            var cmdArgs = parts.Skip(1).ToArray();

            switch (cmd)
            {
                case "quit":
                case "exit":
                case "q":
                    AnsiConsole.MarkupLine("[dim]Bye![/]");
                    return;

                case "use":
                    if (cmdArgs.Length == 0)
                    {
                        AnsiConsole.MarkupLine("[red]Usage:[/] use <table_name>");
                    }
                    else
                    {
                        var newTableName = cmdArgs[0];
                        if (db.Catalog.TableDescriptors.ContainsKey(newTableName))
                        {
                            currentTableName = newTableName;
                            currentTable = db.GetTable(currentTableName);
                            AnsiConsole.MarkupLine($"[green]Switched to table:[/] {currentTableName}");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[red]Error:[/] Table '{newTableName}' not found.");
                        }
                    }
                    break;

                default:
                    await ExecuteCommand(db, currentTable, cmd, cmdArgs);
                    break;
            }
        }
    }

    static async Task ExecuteCommand(ReadOnlyDatabase db, ReadOnlyTable table, string command, string[] args)
    {
        switch (command.ToLowerInvariant())
        {
            case "help":
            case "?":
                PrintHelp();
                break;

            case "get":
                await ExecuteGet(table, args);
                break;

            case "tables":
                ExecuteTables(db);
                break;

            case "info":
                ExecuteInfo(db, table);
                break;

            case "scan":
                await ExecuteScan(table, args);
                break;

            case "keys":
                await ExecuteKeys(table, args);
                break;

            case "values":
                await ExecuteValues(table, args);
                break;

            case "count":
                ExecuteCount(table);
                break;

            case "prefix":
                await ExecutePrefix(table, args);
                break;

            default:
                AnsiConsole.MarkupLine($"[red]Unknown command:[/] {command}");
                AnsiConsole.MarkupLine("[dim]Type 'help' for available commands.[/]");
                break;
        }
    }

    static void PrintHelp()
    {
        var table = new Table();
        table.AddColumn("Command");
        table.AddColumn("Description");
        table.Border = TableBorder.Rounded;

        table.AddRow("[green]get[/] <key>", "Get value by key");
        table.AddRow("[green]scan[/] [[offset]] [[limit]]", "Scan key-value entries (default: offset=0, limit=20)");
        table.AddRow("[green]keys[/] [[offset]] [[limit]]", "Scan keys only");
        table.AddRow("[green]values[/] [[offset]] [[limit]]", "Scan values only");
        table.AddRow("[green]prefix[/] <key> [[limit]]", "Search by key prefix (default: limit=10)");
        table.AddRow("[green]count[/]", "Count all entries");
        table.AddRow("[green]tables[/]", "List all tables");
        table.AddRow("[green]use[/] <table>", "Switch to another table");
        table.AddRow("[green]info[/]", "Show database info");
        table.AddRow("[green]help[/]", "Show this help");
        table.AddRow("[green]quit[/]", "Exit the session");

        AnsiConsole.Write(table);
    }

    static async Task ExecuteGet(ReadOnlyTable table, string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] get <key>");
            return;
        }

        var keyStr = args[0];
        var buffer = ArrayPool<byte>.Shared.Rent(256);
        int bytesWritten;
        while (!table.KeyEncoding.TryEncode(keyStr, buffer, out bytesWritten))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
        }
        var keyBytes = buffer.AsMemory(0, bytesWritten);

        using var result = await table.GetAsync(keyBytes);

        if (!result.HasValue)
        {
            AnsiConsole.MarkupLine("[yellow](nil)[/]");
            return;
        }

        var valueBytes = result.Value.Memory.Span;
        var valueStr = TryDecodeValue(valueBytes);

        if (valueStr != null)
        {
            AnsiConsole.MarkupLine($"[green]{EscapeMarkup(valueStr)}[/]");
        }
        else
        {
            // Show as hex dump
            AnsiConsole.MarkupLine($"[dim](binary, {valueBytes.Length} bytes)[/]");
            PrintHexDump(valueBytes);
        }
    }

    static void ExecuteTables(ReadOnlyDatabase db)
    {
        var table = new Table();
        table.AddColumn("#");
        table.AddColumn("Table Name");
        table.AddColumn("Key Encoding");
        table.AddColumn("Indexes");
        table.Border = TableBorder.Rounded;

        var i = 1;
        foreach (var (name, descriptor) in db.Catalog.TableDescriptors)
        {
            var indexCount = descriptor.IndexDescriptors.Count;
            table.AddRow(
                i.ToString(),
                $"[green]{name}[/]",
                descriptor.PrimaryKeyDescriptor.KeyEncoding.Id,
                indexCount > 0 ? indexCount.ToString() : "-");
            i++;
        }

        AnsiConsole.Write(table);
    }

    static void ExecuteInfo(ReadOnlyDatabase db, ReadOnlyTable currentTable)
    {
        var table = new Table();
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.Border = TableBorder.Rounded;

        table.AddRow("Page Size", $"{db.Catalog.PageSize} bytes");
        table.AddRow("Table Count", db.Catalog.TableDescriptors.Count.ToString());
        table.AddRow("Current Table", currentTable.Name);
        table.AddRow("Key Encoding", currentTable.KeyEncoding.Id);

        if (db.Catalog.Filters is { Count: > 0 })
        {
            var filterNames = string.Join(", ", db.Catalog.Filters.Select(f => f.GetType().Name));
            table.AddRow("Filters", filterNames);
        }

        AnsiConsole.Write(table);
    }

    static async Task ExecuteScan(ReadOnlyTable table, string[] args)
    {
        var offset = 0;
        var limit = 20;

        if (args.Length > 0 && int.TryParse(args[0], out var parsedOffset))
        {
            offset = parsedOffset;
        }
        if (args.Length > 1 && int.TryParse(args[1], out var parsedLimit))
        {
            limit = parsedLimit;
        }

        var iterator = table.CreateIterator();
        var skipped = 0;
        var displayed = 0;

        while (await iterator.MoveNextAsync())
        {
            if (skipped < offset)
            {
                skipped++;
                continue;
            }

            if (displayed >= limit)
            {
                break;
            }

            var key = iterator.CurrentKey;
            var value = iterator.CurrentValue;

            var keyStr = TryDecodeKey(key.Span, table.KeyEncoding);
            var keyDisplay = keyStr != null
                ? EscapeMarkup(Truncate(keyStr, 40))
                : $"(binary, {key.Length} bytes)";

            var valueStr = TryDecodeValue(value.Span);
            var valueDisplay = valueStr != null
                ? EscapeMarkup(Truncate(valueStr, 60))
                : $"(binary, {value.Length} bytes)";

            AnsiConsole.MarkupLine($"[dim]{offset + displayed + 1})[/] [blue]{keyDisplay}[/] -> [green]{valueDisplay}[/]");
            displayed++;
        }

        if (displayed == 0)
        {
            AnsiConsole.MarkupLine("[yellow](empty)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]Displayed {displayed} entries (offset: {offset})[/]");
        }
    }

    static async Task ExecuteKeys(ReadOnlyTable table, string[] args)
    {
        var offset = 0;
        var limit = 20;

        if (args.Length > 0 && int.TryParse(args[0], out var parsedOffset))
        {
            offset = parsedOffset;
        }
        if (args.Length > 1 && int.TryParse(args[1], out var parsedLimit))
        {
            limit = parsedLimit;
        }

        var iterator = table.CreateIterator();
        var skipped = 0;
        var displayed = 0;

        while (await iterator.MoveNextAsync())
        {
            if (skipped < offset)
            {
                skipped++;
                continue;
            }

            if (displayed >= limit)
            {
                break;
            }

            var key = iterator.CurrentKey;
            var keyStr = TryDecodeKey(key.Span, table.KeyEncoding);
            var keyDisplay = keyStr != null
                ? EscapeMarkup(keyStr)
                : $"(binary, {key.Length} bytes)";

            AnsiConsole.MarkupLine($"[dim]{offset + displayed + 1})[/] [blue]{keyDisplay}[/]");
            displayed++;
        }

        if (displayed == 0)
        {
            AnsiConsole.MarkupLine("[yellow](empty)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]Displayed {displayed} keys (offset: {offset})[/]");
        }
    }

    static async Task ExecuteValues(ReadOnlyTable table, string[] args)
    {
        var offset = 0;
        var limit = 20;

        if (args.Length > 0 && int.TryParse(args[0], out var parsedOffset))
        {
            offset = parsedOffset;
        }
        if (args.Length > 1 && int.TryParse(args[1], out var parsedLimit))
        {
            limit = parsedLimit;
        }

        var iterator = table.CreateIterator();
        var skipped = 0;
        var displayed = 0;

        while (await iterator.MoveNextAsync())
        {
            if (skipped < offset)
            {
                skipped++;
                continue;
            }

            if (displayed >= limit)
            {
                break;
            }

            var value = iterator.CurrentValue;
            var valueStr = TryDecodeValue(value.Span);
            var valueDisplay = valueStr != null
                ? EscapeMarkup(valueStr)
                : $"(binary, {value.Length} bytes)";

            AnsiConsole.MarkupLine($"[dim]{offset + displayed + 1})[/] [green]{valueDisplay}[/]");
            displayed++;
        }

        if (displayed == 0)
        {
            AnsiConsole.MarkupLine("[yellow](empty)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]Displayed {displayed} values (offset: {offset})[/]");
        }
    }

    static void ExecuteCount(ReadOnlyTable table)
    {
        var iterator = table.CreateIterator();
        var count = 0;

        while (iterator.MoveNext())
        {
            count++;
        }

        AnsiConsole.MarkupLine($"[green](integer)[/] {count}");
    }

    static async Task ExecutePrefix(ReadOnlyTable table, string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] prefix <key> [[limit]]");
            return;
        }

        var keyStr = args[0];
        var limit = 10;
        if (args.Length > 1 && int.TryParse(args[1], out var parsedLimit))
        {
            limit = parsedLimit;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(256);
        int bytesWritten;
        while (!table.KeyEncoding.TryEncode(keyStr, buffer, out bytesWritten))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
        }
        var prefixBytes = buffer.AsMemory(0, bytesWritten);

        try
        {
            var iterator = table.CreateIterator();
            if (!await iterator.TrySeekAsync(prefixBytes))
            {
                AnsiConsole.MarkupLine("[yellow](empty)[/]");
                return;
            }

            var displayed = 0;
            do
            {
                if (displayed >= limit) break;

                var key = iterator.CurrentKey;
                // Stop if key no longer starts with the prefix
                if (key.Length < prefixBytes.Length ||
                    !key.Span[..prefixBytes.Length].SequenceEqual(prefixBytes.Span))
                {
                    break;
                }

                var value = iterator.CurrentValue;

                var keyDisplay = TryDecodeKey(key.Span, table.KeyEncoding);
                var keyText = keyDisplay != null
                    ? EscapeMarkup(Truncate(keyDisplay, 40))
                    : $"(binary, {key.Length} bytes)";

                var valueStr = TryDecodeValue(value.Span);
                var valueDisplay = valueStr != null
                    ? EscapeMarkup(Truncate(valueStr, 60))
                    : $"(binary, {value.Length} bytes)";

                AnsiConsole.MarkupLine($"[dim]{displayed + 1})[/] [blue]{keyText}[/] -> [green]{valueDisplay}[/]");
                displayed++;
            }
            while (await iterator.MoveNextAsync());

            if (displayed == 0)
            {
                AnsiConsole.MarkupLine("[yellow](empty)[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]Displayed {displayed} entries with prefix '{EscapeMarkup(keyStr)}'[/]");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static string[] ParseCommand(string input)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var escapeNext = false;

        foreach (var c in input)
        {
            if (escapeNext)
            {
                current.Append(c);
                escapeNext = false;
                continue;
            }

            if (c == '\\')
            {
                escapeNext = true;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts.ToArray();
    }

    static string? TryDecodeKey(ReadOnlySpan<byte> bytes, IKeyEncoding keyEncoding)
    {
        Span<byte> buffer = stackalloc byte[256];
        if (keyEncoding.TryFormat(bytes, buffer, out var bytesWritten))
        {
            return Encoding.UTF8.GetString(buffer[..bytesWritten]);
        }
        return TryDecodeAsUtf8(bytes);
    }

    static string? TryDecodeValue(ReadOnlySpan<byte> bytes)
    {
        // Try MessagePack first
        // var jsonStr = TryDecodeAsMessagePackJson(bytes);
        // if (jsonStr != null)
        //     return jsonStr;

        // Fall back to UTF-8 string
        return TryDecodeAsUtf8(bytes);
    }

    static string? TryDecodeAsMessagePackJson(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return MessagePackSerializer.ConvertToJson(bytes.ToArray());
        }
        catch
        {
            return null;
        }
    }

    static string? TryDecodeAsUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var str = Encoding.UTF8.GetString(bytes);
            // Check if all characters are printable
            foreach (var c in str)
            {
                if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                    return null;
            }
            return str;
        }
        catch
        {
            return null;
        }
    }

    static void PrintHexDump(ReadOnlySpan<byte> bytes)
    {
        const int bytesPerLine = 16;
        for (var i = 0; i < bytes.Length; i += bytesPerLine)
        {
            var line = bytes.Slice(i, Math.Min(bytesPerLine, bytes.Length - i));
            var hex = Convert.ToHexString(line);
            var hexFormatted = string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(j => hex.Substring(j * 2, 2)));
            AnsiConsole.MarkupLine($"[dim]{i:X8}[/]  {hexFormatted}");

            if (i >= 64)
            {
                AnsiConsole.MarkupLine($"[dim]... ({bytes.Length - i} more bytes)[/]");
                break;
            }
        }
    }

    static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }

    static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }
}

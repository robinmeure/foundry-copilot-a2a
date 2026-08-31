using System.Diagnostics;

namespace FoundryCopilotA2A.Cli;

internal sealed record CommandResult(string StandardOutput, string StandardError);

internal sealed class ExternalCommandException(
    string fileName,
    int exitCode,
    string standardError)
    : Exception(BuildMessage(fileName, exitCode, standardError))
{
    private static string BuildMessage(string fileName, int exitCode, string standardError)
    {
        var detail = string.IsNullOrWhiteSpace(standardError)
            ? "No error output was returned."
            : standardError.Trim();

        if (detail.Length > 4000)
        {
            detail = $"{detail[..4000]}...";
        }

        return $"{fileName} exited with code {exitCode}. {detail}";
    }
}

internal sealed class ProcessRunner
{
    public async Task<CommandResult> CaptureAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null)
    {
        using var process = CreateProcess(
            fileName,
            arguments,
            redirectOutput: true,
            environment,
            workingDirectory);

        Start(process, fileName);
        using var cancellationRegistration = cancellationToken.Register(
            () => TryKill(process));

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var result = new CommandResult(await standardOutput, await standardError);
        if (process.ExitCode != 0)
        {
            throw new ExternalCommandException(fileName, process.ExitCode, result.StandardError);
        }

        return result;
    }

    public async Task RunInteractiveAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null)
    {
        using var process = CreateProcess(
            fileName,
            arguments,
            redirectOutput: false,
            environment,
            workingDirectory);

        Start(process, fileName);
        using var cancellationRegistration = cancellationToken.Register(
            () => TryKill(process));

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new ExternalCommandException(fileName, process.ExitCode, string.Empty);
        }
    }

    private static Process CreateProcess(
        string fileName,
        IEnumerable<string> arguments,
        bool redirectOutput,
        IReadOnlyDictionary<string, string?>? environment,
        string? workingDirectory)
    {
        var resolved = ExecutableResolver.Resolve(fileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = resolved.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = redirectOutput,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        foreach (var prefixArgument in resolved.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        return new Process { StartInfo = startInfo };
    }

    private static void Start(Process process, string fileName)
    {
        try
        {
            process.Start();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new CliException(
                $"Could not start '{fileName}'. Verify that it is installed and available on PATH.");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }
}

internal sealed record ResolvedExecutable(
    string FileName,
    IReadOnlyList<string> PrefixArguments);

internal static class ExecutableResolver
{
    public static ResolvedExecutable Resolve(string fileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ResolvedExecutable(fileName, []);
        }

        var resolvedPath = FindOnWindowsPath(fileName);
        var extension = Path.GetExtension(resolvedPath);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedExecutable(resolvedPath, []);
        }

        // The Windows Azure CLI launcher is a batch file that forwards `%*` to its bundled
        // Python. Invoking that batch layer would make cmd.exe re-parse JSON and user input,
        // so call the same Python module directly instead.
        if (Path.GetFileName(resolvedPath).Equals(
                "az.cmd", StringComparison.OrdinalIgnoreCase))
        {
            var python = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(resolvedPath)!,
                "..",
                "python.exe"));
            if (File.Exists(python))
            {
                return new ResolvedExecutable(
                    python,
                    ["-IBm", "azure.cli"]);
            }
        }

        throw new CliException(
            $"'{fileName}' resolves to a batch file, which cannot be launched safely. " +
            "Use a native executable or invoke the underlying runtime directly.");
    }

    private static string FindOnWindowsPath(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName) ||
            fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(fileName)
                ? Path.GetFullPath(fileName)
                : fileName;
        }

        var extensions = Path.HasExtension(fileName)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ??
               ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleanDirectory = directory.Trim().Trim('"');
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(cleanDirectory, fileName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fileName;
    }
}

using System.Diagnostics;
using System.IO;

namespace Browser.Services.AI
{
    /// <summary>
    /// Infrastructure for the coding agent feature.
    /// All dangerous actions require user confirmation before execution.
    /// </summary>
    public class CodingAgentService
    {
        /// <summary>
        /// Represents a coding action that requires user confirmation.
        /// </summary>
        public class CodingAction
        {
            public CodingActionType Type { get; set; }
            public string Description { get; set; } = string.Empty;
            public string? FilePath { get; set; }
            public string? Content { get; set; }
            public string? Command { get; set; }
            public bool IsDangerous { get; set; }
        }

        /// <summary>
        /// Types of coding actions the agent can perform.
        /// </summary>
        public enum CodingActionType
        {
            ReadFile,
            WriteFile,
            EditFile,
            DeleteFile,
            RunCommand,
            AnalyzeCode,
            GitDiff,
            ListDirectory
        }

        /// <summary>
        /// Event fired when an action requires user confirmation.
        /// The handler should return true to allow, false to deny.
        /// </summary>
        public event Func<CodingAction, Task<bool>>? ConfirmationRequired;

        /// <summary>Working directory for file operations.</summary>
        public string WorkingDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Read a file's content. Requires confirmation.
        /// </summary>
        public async Task<string?> ReadFileAsync(string filePath)
        {
            var fullPath = ResolvePath(filePath);
            var action = new CodingAction
            {
                Type = CodingActionType.ReadFile,
                Description = $"Datei lesen: {fullPath}",
                FilePath = fullPath,
                IsDangerous = false
            };

            if (!await RequestConfirmation(action)) return null;

            try
            {
                return await File.ReadAllTextAsync(fullPath);
            }
            catch (Exception ex)
            {
                return $"[Fehler beim Lesen: {ex.Message}]";
            }
        }

        /// <summary>
        /// Write content to a file. Requires confirmation (dangerous).
        /// </summary>
        public async Task<bool> WriteFileAsync(string filePath, string content)
        {
            var fullPath = ResolvePath(filePath);
            var action = new CodingAction
            {
                Type = CodingActionType.WriteFile,
                Description = $"Datei schreiben: {fullPath}\nInhalt: {content.Length} Zeichen",
                FilePath = fullPath,
                Content = content,
                IsDangerous = true
            };

            if (!await RequestConfirmation(action)) return false;

            try
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(fullPath, content);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// List files in a directory. Requires confirmation.
        /// </summary>
        public async Task<string?> ListDirectoryAsync(string path)
        {
            var fullPath = ResolvePath(path);
            var action = new CodingAction
            {
                Type = CodingActionType.ListDirectory,
                Description = $"Verzeichnis auflisten: {fullPath}",
                FilePath = fullPath,
                IsDangerous = false
            };

            if (!await RequestConfirmation(action)) return null;

            try
            {
                var entries = new List<string>();
                foreach (var dir in Directory.GetDirectories(fullPath))
                    entries.Add($"📁 {Path.GetFileName(dir)}/");
                foreach (var file in Directory.GetFiles(fullPath))
                    entries.Add($"📄 {Path.GetFileName(file)}");
                return string.Join("\n", entries);
            }
            catch (Exception ex)
            {
                return $"[Fehler: {ex.Message}]";
            }
        }

        /// <summary>
        /// Run a shell command. Requires confirmation (dangerous).
        /// </summary>
        public async Task<string?> RunCommandAsync(string command)
        {
            var action = new CodingAction
            {
                Type = CodingActionType.RunCommand,
                Description = $"Befehl ausführen: {command}",
                Command = command,
                IsDangerous = true
            };

            if (!await RequestConfirmation(action)) return null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    WorkingDirectory = string.IsNullOrEmpty(WorkingDirectory)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                        : WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return "[Prozess konnte nicht gestartet werden]";

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var result = output;
                if (!string.IsNullOrEmpty(error))
                    result += $"\n[STDERR]: {error}";
                result += $"\n[Exit-Code: {process.ExitCode}]";

                return result;
            }
            catch (Exception ex)
            {
                return $"[Fehler: {ex.Message}]";
            }
        }

        /// <summary>
        /// Run git diff in the working directory. Requires confirmation.
        /// </summary>
        public async Task<string?> GitDiffAsync()
        {
            return await RunCommandAsync("git diff");
        }

        private string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(
                string.IsNullOrEmpty(WorkingDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : WorkingDirectory,
                path);
        }

        private async Task<bool> RequestConfirmation(CodingAction action)
        {
            if (ConfirmationRequired == null) return false; // Deny if no handler is registered
            return await ConfirmationRequired(action);
        }
    }
}

using Spectre.Console;
using System;
using System.IO;
using System.Text.Json;

namespace SyncJob
{
    public static class Log
    {
        public enum Level { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4, Fatal = 5 }

        public sealed class Options
        {
            public bool EnableConsole { get; set; } = true;
            public string? FilePath { get; set; }
            public string? DirectoryPath { get; set; }
            public bool JsonFormat { get; set; } = false;
            public Level MinLevel { get; set; } = Level.Info;
            public string? AppName { get; set; } = "SyncJob";
        }

        private static readonly object _sync = new object();
        private static Options _options = new Options();

        public static void Init(Options options)
        {
            _options = options ?? new Options();
            EnsureFilePath();
        }

        public static void Shutdown()
        {
            // No-op for now
        }

        public static void Trace(string message, Exception? ex = null, string? evt = null) => Write(Level.Trace, message, ex, evt);
        public static void Debug(string message, Exception? ex = null, string? evt = null) => Write(Level.Debug, message, ex, evt);
        public static void Info(string message, Exception? ex = null, string? evt = null) => Write(Level.Info, message, ex, evt);
        public static void Warn(string message, Exception? ex = null, string? evt = null) => Write(Level.Warn, message, ex, evt);
        public static void Error(string message, Exception? ex = null, string? evt = null) => Write(Level.Error, message, ex, evt);
        public static void Fatal(string message, Exception? ex = null, string? evt = null) => Write(Level.Fatal, message, ex, evt);

        private static void Write(Level level, string message, Exception? ex, string? evt)
        {
            if (level < _options.MinLevel) return;

            var now = DateTimeOffset.Now;
            var entry = new LogEntry
            {
                ts = now,
                level = level.ToString().ToUpperInvariant(),
                app = _options.AppName,
                evt = evt,
                msg = message,
                ex = ex?.ToString()
            };

            string line = _options.JsonFormat
                ? JsonSerializer.Serialize(entry)
                : $"{entry.ts:O} [{entry.level}] {entry.evt ?? "-"} {entry.msg}{(ex != null ? " | " + ex.GetType().Name : string.Empty)}";

            lock (_sync)
            {
                if (_options.EnableConsole)
                {
                    var style = level switch
                    {
                        Level.Trace => "grey",
                        Level.Debug => "deepskyblue1",
                        Level.Info => "white",
                        Level.Warn => "yellow",
                        Level.Error => "red",
                        Level.Fatal => "bold red",
                        _ => "white"
                    };
                    AnsiConsole.MarkupLine($"[{style}]{EscapeMarkup(line)}[/]");
                }

                if (!string.IsNullOrWhiteSpace(_options.FilePath))
                {
                    try
                    {
                        File.AppendAllText(_options.FilePath!, line + Environment.NewLine);
                    }
                    catch
                    {
                        // Swallow file IO to not break CLI
                    }
                }
            }
        }

        private static string EscapeMarkup(string text)
        {
            return text?.Replace("[", "[[").Replace("]", "]]" ) ?? string.Empty;
        }

        private static void EnsureFilePath()
        {
            if (!string.IsNullOrWhiteSpace(_options.FilePath))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(_options.FilePath)) ?? ".";
                Directory.CreateDirectory(dir);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_options.DirectoryPath))
            {
                var dir = _options.DirectoryPath!;
                Directory.CreateDirectory(dir);
                var name = ($"{_options.AppName}_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                _options.FilePath = Path.Combine(dir, name);
            }
        }

        private sealed class LogEntry
        {
            public DateTimeOffset ts { get; set; }
            public string level { get; set; } = string.Empty;
            public string? app { get; set; }
            public string? evt { get; set; }
            public string msg { get; set; } = string.Empty;
            public string? ex { get; set; }
        }
    }
}


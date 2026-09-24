using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ZeroCue.DataProbe.Services
{
    internal static class ZeroCueLog
    {
        private const long MaximumLogFileBytes = 4L * 1024L * 1024L;
        private const int RetainedFileCount = 3;
        private static readonly object InitLock = new();
        private static readonly object CommunicationLock = new();
        private static readonly object InputMappingLock = new();
        private static StreamWriter? _communicationWriter;
        private static StreamWriter? _inputMappingWriter;
        private static bool _initialized;

        public static string CommunicationLogPath =>
            Path.Combine(ZeroCuePaths.LogsDirectory, "zerocue-communication.log");

        public static string InputMappingLogPath =>
            Path.Combine(ZeroCuePaths.LogsDirectory, "zerocue-input-mappings.log");

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            lock (InitLock)
            {
                if (_initialized)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(ZeroCuePaths.LogsDirectory);
                    _communicationWriter = CreateWriter(CommunicationLogPath);
                    _inputMappingWriter = CreateWriter(InputMappingLogPath);
                    WriteHeader(_communicationWriter, "ZeroCue communication log (wired and wireless)");
                    WriteHeader(_inputMappingWriter, "ZeroCue input and mapping log");
                    _initialized = true;
                }
                catch
                {
                    _communicationWriter?.Dispose();
                    _inputMappingWriter?.Dispose();
                    _communicationWriter = null;
                    _inputMappingWriter = null;
                    // Logging must never prevent the application from starting.
                }
            }
        }

        public static void Shutdown()
        {
            lock (InitLock)
            {
                lock (CommunicationLock)
                {
                    _communicationWriter?.Dispose();
                    _communicationWriter = null;
                }

                lock (InputMappingLock)
                {
                    _inputMappingWriter?.Dispose();
                    _inputMappingWriter = null;
                }

                _initialized = false;
            }
        }

        public static void Communication(string message)
        {
            Write(CommunicationLock, isCommunicationLog: true, message);
        }

        public static void InputMapping(string message)
        {
            Write(InputMappingLock, isCommunicationLog: false, message);
        }

        private static void Write(object sync, bool isCommunicationLog, string message)
        {
            Initialize();

            try
            {
                lock (sync)
                {
                    var writer = isCommunicationLog ? _communicationWriter : _inputMappingWriter;
                    if (writer == null)
                    {
                        return;
                    }

                    var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}";
                    var pendingBytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                    if (writer.BaseStream.Length + pendingBytes > MaximumLogFileBytes)
                    {
                        if (isCommunicationLog)
                        {
                            RotateWriter(ref _communicationWriter, CommunicationLogPath, "ZeroCue communication log (wired and wireless)");
                            writer = _communicationWriter;
                        }
                        else
                        {
                            RotateWriter(ref _inputMappingWriter, InputMappingLogPath, "ZeroCue input and mapping log");
                            writer = _inputMappingWriter;
                        }
                    }

                    writer?.WriteLine(line);
                }
            }
            catch
            {
                // Logging is best effort and must not affect controller processing.
            }
        }

        private static void RotateWriter(ref StreamWriter? writer, string path, string title)
        {
            writer?.Dispose();
            writer = null;

            try
            {
                for (var index = RetainedFileCount - 1; index >= 1; index--)
                {
                    var destination = $"{path}.{index}";
                    var source = index == 1 ? path : $"{path}.{index - 1}";
                    if (File.Exists(source))
                    {
                        File.Move(source, destination, overwrite: true);
                    }
                }
            }
            catch
            {
                // If backup rotation is blocked, truncate the active segment below
                // rather than permanently disabling logging.
            }

            writer = CreateWriter(path);
            WriteHeader(writer, title);
            writer.WriteLine($"=== Log rotated: maximum segment size {MaximumLogFileBytes / (1024 * 1024)} MiB; retained segments {RetainedFileCount} ===");
        }

        private static StreamWriter CreateWriter(string path)
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
        }

        private static void WriteHeader(StreamWriter writer, string title)
        {
            var assembly = typeof(ZeroCueLog).Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            writer.WriteLine($"=== {title} ===");
            writer.WriteLine($"=== Session started: {DateTimeOffset.Now:O} ===");
            writer.WriteLine($"=== Version: {version} ===");
            writer.WriteLine($"=== Environment: {RuntimeInformation.OSDescription}; process={RuntimeInformation.ProcessArchitecture}; framework={RuntimeInformation.FrameworkDescription} ===");
        }
    }
}

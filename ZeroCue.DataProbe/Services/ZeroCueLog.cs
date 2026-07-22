using System;
using System.IO;
using System.Text;

namespace ZeroCue.DataProbe.Services
{
    internal static class ZeroCueLog
    {
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
            Write(CommunicationLock, _communicationWriter, message);
        }

        public static void InputMapping(string message)
        {
            Write(InputMappingLock, _inputMappingWriter, message);
        }

        private static void Write(object sync, StreamWriter? writer, string message)
        {
            Initialize();

            writer ??= ReferenceEquals(sync, CommunicationLock)
                ? _communicationWriter
                : _inputMappingWriter;

            try
            {
                lock (sync)
                {
                    writer?.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}");
                }
            }
            catch
            {
                // Logging is best effort and must not affect controller processing.
            }
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
            writer.WriteLine($"=== {title} ===");
            writer.WriteLine($"=== Session started: {DateTimeOffset.Now:O} ===");
        }
    }
}

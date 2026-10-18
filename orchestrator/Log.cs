using System;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CasuMpOrchestrator;

internal static class Log
{
    private static Action<string>? _relay;

    public static void Init(Action<string>? relay)
    {
        _relay = relay;
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new ConsoleSink())
            .CreateLogger();
    }

    public static void SetRelay(Action<string> relay) => _relay = relay;

    public static void ConsoleOnly(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss} orch] {message}");

    public static void Relay(string message) =>
        _relay?.Invoke($"[{DateTime.Now:HH:mm:ss} orch] {message}");

    public static void Info(string message) => Serilog.Log.Information("{Line}", message);

    public static void Error(string message) => Serilog.Log.Error("{Line}", message);

    public static void Error(Exception exception, string message) =>
        Serilog.Log.Error(exception, "{Line}", message);

    private sealed class ConsoleSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            string message = logEvent.Properties.TryGetValue("Line", out LogEventPropertyValue? value)
                && value is ScalarValue scalar
                && scalar.Value is string text
                    ? text
                    : logEvent.RenderMessage();
            if (logEvent.Exception != null)
            {
                message += Environment.NewLine + logEvent.Exception;
            }

            string line = $"[{logEvent.Timestamp:HH:mm:ss} orch] {message}";
            Console.WriteLine(line);
            _relay?.Invoke(line);
        }
    }
}

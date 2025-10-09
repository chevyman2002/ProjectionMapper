using System;
using Microsoft.Extensions.Logging;

namespace ProjectionMapper.Logging
{
    /// <summary>
    /// Helper to configure a basic logger factory.
    /// For production use you should configure file sinks and levels via appsettings and DI.
    /// </summary>
    public static class LoggingSetup
    {
        public static ILoggerFactory CreateLoggerFactory()
        {
            var factory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                // Optionally add file or other sinks here (third-party sinks required).
                builder.SetMinimumLevel(LogLevel.Information);
            });

            return factory;
        }

        public static ILogger CreateLogger<T>()
        {
            var factory = CreateLoggerFactory();
            return factory.CreateLogger<T>();
        }
    }
}
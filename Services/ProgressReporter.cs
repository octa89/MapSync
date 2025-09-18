using System;
using Microsoft.Extensions.Logging;

namespace WpfMapApp1.Services
{
    public class ProgressReporter : IProgressReporter
    {
        private readonly ILogger<ProgressReporter> _logger;
        public event EventHandler<ProgressEventArgs>? ProgressChanged;

        public ProgressReporter(ILogger<ProgressReporter> logger)
        {
            _logger = logger;
        }

        public void Report(string message, double? percentage = null)
        {
            _logger.LogInformation("Progress: {Message} {Percentage}%", message, percentage?.ToString("F0") ?? "");
            ProgressChanged?.Invoke(this, new ProgressEventArgs(message, percentage));
        }

        public void ReportComplete(string? message = null)
        {
            var completeMessage = message ?? "Operation completed";
            _logger.LogInformation("Completed: {Message}", completeMessage);
            ProgressChanged?.Invoke(this, new ProgressEventArgs(completeMessage, 100, true));
        }

        public void ReportError(string message)
        {
            _logger.LogError("Error: {Message}", message);
            ProgressChanged?.Invoke(this, new ProgressEventArgs(message, null, false, true));
        }
    }
}
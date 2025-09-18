using System;

namespace WpfMapApp1.Services
{
    public interface IProgressReporter
    {
        event EventHandler<ProgressEventArgs> ProgressChanged;
        void Report(string message, double? percentage = null);
        void ReportComplete(string? message = null);
        void ReportError(string message);
    }

    public class ProgressEventArgs : EventArgs
    {
        public string Message { get; set; }
        public double? Percentage { get; set; }
        public bool IsComplete { get; set; }
        public bool IsError { get; set; }

        public ProgressEventArgs(string message, double? percentage = null, bool isComplete = false, bool isError = false)
        {
            Message = message;
            Percentage = percentage;
            IsComplete = isComplete;
            IsError = isError;
        }
    }
}
using Serilog.Core;
using Serilog.Events;
using System.Windows.Controls;
using Serilog.Configuration;
using Serilog;

namespace SRXMDL
{
    public class TextBoxSink : ILogEventSink
    {
        private readonly TextBox _textBox;
        private readonly IFormatProvider? _formatProvider;

        public TextBoxSink(TextBox textBox, IFormatProvider? formatProvider = null)
        {
            _textBox = textBox;
            _formatProvider = formatProvider;
        }

        public void Emit(LogEvent logEvent)
        {
            var message = logEvent.RenderMessage(_formatProvider);
            var formattedMessage = $"[{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss}] [{logEvent.Level}] {message}\n";

            _textBox.Dispatcher.Invoke(() =>
            {
                _textBox.AppendText(formattedMessage);
                _textBox.ScrollToEnd();
            });
        }
    }

    public static class TextBoxSinkExtensions
    {
        public static LoggerConfiguration TextBox(
            this LoggerSinkConfiguration loggerConfiguration,
            TextBox textBox,
            IFormatProvider? formatProvider = null)
        {
            return loggerConfiguration.Sink(new TextBoxSink(textBox, formatProvider));
        }
    }
} 
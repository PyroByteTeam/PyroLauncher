using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PyroLauncher.Instances;

public enum LogLevel { Info, Warn, Error, Debug, Launcher }

public sealed class LogLine(string text, LogLevel level)
{
    public string Text { get; } = text;
    public LogLevel Level { get; } = level;
    public override string ToString() => Text;
}

/// <summary>
/// Turns raw game stdout into readable lines. Minecraft's own log4j config prints XML events
/// (<c>&lt;log4j:Event …&gt;</c>); those are reassembled into "[time] [thread/LEVEL] [logger]: message".
/// </summary>
public sealed partial class GameLogParser
{
    private StringBuilder? _event;

    [GeneratedRegex(@"(\w+)=""([^""]*)""")]
    private static partial Regex AttrRegex();
    [GeneratedRegex(@"<log4j:Message><!\[CDATA\[(.*?)\]\]></log4j:Message>", RegexOptions.Singleline)]
    private static partial Regex MessageRegex();
    [GeneratedRegex(@"<log4j:Throwable><!\[CDATA\[(.*?)\]\]></log4j:Throwable>", RegexOptions.Singleline)]
    private static partial Regex ThrowableRegex();
    [GeneratedRegex(@"[\[/ ](INFO|WARN|WARNING|ERROR|FATAL|SEVERE|DEBUG|TRACE)[\]:]")]
    private static partial Regex LevelRegex();

    public void Reset() => _event = null;

    public IEnumerable<LogLine> Feed(string raw)
    {
        var trimmed = raw.TrimStart();
        if (_event == null && trimmed.StartsWith("<log4j:Event"))
            _event = new StringBuilder();

        if (_event != null)
        {
            _event.AppendLine(raw);
            if (!raw.Contains("</log4j:Event>")) return [];
            var xml = _event.ToString();
            _event = null;
            return ParseEvent(xml);
        }
        return [Classify(raw)];
    }

    private static List<LogLine> ParseEvent(string xml)
    {
        var header = xml[..Math.Max(0, xml.IndexOf('>'))];
        var attrs = AttrRegex().Matches(header).ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
        var levelText = attrs.GetValueOrDefault("level", "INFO");
        var level = ToLevel(levelText);
        var time = attrs.TryGetValue("timestamp", out var ts) && long.TryParse(ts, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss")
            : DateTime.Now.ToString("HH:mm:ss");
        var logger = attrs.GetValueOrDefault("logger", "");
        var shortLogger = logger.Contains('.') ? logger[(logger.LastIndexOf('.') + 1)..] : logger;
        var thread = attrs.GetValueOrDefault("thread", "main");

        var msg = MessageRegex().Match(xml) is { Success: true } mm ? mm.Groups[1].Value : "";
        var lines = new List<LogLine>();
        var msgLines = msg.Replace("\r", "").Split('\n');
        lines.Add(new LogLine($"[{time}] [{thread}/{levelText}] [{shortLogger}]: {msgLines[0]}", level));
        for (int i = 1; i < msgLines.Length; i++)
            if (msgLines[i].Length > 0) lines.Add(new LogLine(msgLines[i], level));

        if (ThrowableRegex().Match(xml) is { Success: true } tm)
            foreach (var t in tm.Groups[1].Value.Replace("\r", "").Split('\n'))
                if (t.Length > 0) lines.Add(new LogLine(t, LogLevel.Error));
        return lines;
    }

    public static LogLine Classify(string raw)
    {
        if (raw.StartsWith("[PyroLauncher]")) return new LogLine(raw, LogLevel.Launcher);
        if (raw.StartsWith("[installer]")) return new LogLine(raw, LogLevel.Debug);
        var m = LevelRegex().Match(raw.Length > 120 ? raw[..120] : raw);
        if (m.Success) return new LogLine(raw, ToLevel(m.Groups[1].Value));
        var t = raw.TrimStart();
        if (t.StartsWith("at ") || t.StartsWith("Caused by:") || raw.Contains("Exception") || t.StartsWith("... "))
            return new LogLine(raw, LogLevel.Error);
        return new LogLine(raw, LogLevel.Info);
    }

    private static LogLevel ToLevel(string s) => s.ToUpperInvariant() switch
    {
        "WARN" or "WARNING" => LogLevel.Warn,
        "ERROR" or "FATAL" or "SEVERE" => LogLevel.Error,
        "DEBUG" or "TRACE" => LogLevel.Debug,
        _ => LogLevel.Info,
    };
}

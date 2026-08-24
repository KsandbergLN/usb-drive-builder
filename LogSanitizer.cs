using System.Text.RegularExpressions;

namespace LaptopQaUsbBuilder;

public static partial class LogSanitizer
{
    public static string SanitizeException(Exception exception) => SanitizeStackTrace(exception.ToString());

    public static string SanitizeStackTrace(string value) => StackFrameSourcePath().Replace(value, match =>
        $"{match.Groups["prefix"].Value}{match.Groups["file"].Value}:line {match.Groups["line"].Value}");

    [GeneratedRegex(@"(?m)(?<prefix>\s+in\s+)(?:[A-Za-z]:\\|/)(?:[^\r\n]*[\\/])(?<file>[^\\/\r\n:]+):line\s+(?<line>\d+)")]
    private static partial Regex StackFrameSourcePath();
}

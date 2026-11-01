using System.Text.Json;

namespace FalconLoad.Core.Model;

internal static class JsonErrors
{
    /// <summary>
    /// Whether reading JSON failed because the JSON itself is bad. System.Text.Json reports most problems with a
    /// <see cref="JsonException"/>, but a <c>\u</c> escape that isn't valid UTF-16, such as a lone <c>\ud800</c>, only
    /// shows when a string or property name is read, and then as an <see cref="InvalidOperationException"/>; so does
    /// reading a value as the wrong kind. Code that reads JSON it didn't write treats both alike.
    /// </summary>
    /// <remarks>System.Text.Json sets the source of such an exception to its own name, or to <c>System.Text.Json.Rethrowable</c>.</remarks>
    public static bool IsInvalidJson(Exception ex) =>
        ex is JsonException || ex is InvalidOperationException && ex.Source?.StartsWith("System.Text.Json", StringComparison.Ordinal) == true;
}

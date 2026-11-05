using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Which local DLP alerts a sync response allows the client to delete.
/// A present <c>acceptedIds</c> array is the whole answer, including when it is empty.
/// A missing or unreadable property deletes nothing.
/// </summary>
public static class AlertAcceptDecision
{
    public static bool TrySelectIds(string? body, out IReadOnlyList<string> ids)
    {
        ids = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (!doc.RootElement.TryGetProperty("acceptedIds", out var arr))
                return false;
            if (arr.ValueKind != JsonValueKind.Array)
                return false;

            var list = new List<string>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String)
                    continue;
                var id = el.GetString();
                if (!string.IsNullOrEmpty(id))
                    list.Add(id);
            }

            ids = list;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

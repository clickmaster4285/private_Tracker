namespace client.Services.FaceVerify;

/// <summary>Enrollment / first-verify pose order (matches web FACE_POSES).</summary>
public static class FacePoseCatalog
{
    public static readonly (string Id, string Label)[] Poses =
    {
        ("front", "Look straight at the camera"),
        ("left_slight", "Turn slightly left"),
        ("left", "Turn further left"),
        ("right_slight", "Turn slightly right"),
        ("right", "Turn further right"),
        ("up", "Look slightly upward"),
        ("down", "Look slightly downward"),
        ("front_return", "Return to facing the camera"),
    };

    public static string Label(string poseId)
    {
        foreach (var p in Poses)
        {
            if (string.Equals(p.Id, poseId, StringComparison.OrdinalIgnoreCase))
                return p.Label;
        }
        return poseId.Replace('_', ' ');
    }

    public static IReadOnlyList<string> OrderedPoseIds(IEnumerable<string?> available)
    {
        var set = new HashSet<string>(
            available.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!),
            StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var p in Poses)
        {
            if (set.Contains(p.Id))
                ordered.Add(p.Id);
        }
        foreach (var extra in set)
        {
            if (!ordered.Contains(extra, StringComparer.OrdinalIgnoreCase))
                ordered.Add(extra);
        }
        return ordered;
    }
}

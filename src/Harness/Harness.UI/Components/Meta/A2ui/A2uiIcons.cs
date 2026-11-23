namespace Harness.UI.Components.Meta.A2ui;

/// <summary>
/// Built-in A2UI icon set: SVG path data we wrote (24x24 viewBox, drawn with stroke, no fill).
/// The model only ever picks a name; it never supplies path data. Unknown name → null.
/// </summary>
public static class A2uiIcons
{
    private const string Circle = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z";
    private const string Star = "M12 3.5l2.6 5.4 5.9.8-4.3 4.1 1 5.9L12 17l-5.2 2.7 1-5.9-4.3-4.1 5.9-.8L12 3.5z";
    private const string ArrowLeft = "M19 12H5M11 6l-6 6 6 6";
    private const string ArrowRight = "M5 12h14M13 6l6 6-6 6";
    private const string Check = "M5 12.5l4.5 4.5L19 7";
    private const string X = "M6 6l12 12M18 6L6 18";
    private const string Plus = "M12 5v14M5 12h14";
    private const string Minus = "M5 12h14";
    private const string Info = Circle + "M12 11v6M12 7.5v.01";
    private const string Warning = "M12 4L2.5 20h19L12 4zM12 10v4.5M12 17.5v.01";
    private const string Error = Circle + "M12 7.5v5.5M12 16.5v.01";
    private const string StarHalf = Star + "M12 3.5V17";
    private const string Calendar = "M5 5h14a1 1 0 0 1 1 1v13a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1zM4 10h16M8 3v4M16 3v4";
    private const string User = "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM4.5 20a7.5 7.5 0 0 1 15 0";
    private const string Folder = "M3 7a1 1 0 0 1 1-1h5l2 2h9a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7z";
    private const string Search = "M10.5 17a6.5 6.5 0 1 0 0-13 6.5 6.5 0 0 0 0 13zM20 20l-4.8-4.8";
    private const string Settings = "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1";

    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["check"] = Check,
        ["x"] = X,
        ["plus"] = Plus,
        ["minus"] = Minus,
        ["arrow-up"] = "M12 19V5M6 11l6-6 6 6",
        ["arrow-down"] = "M12 5v14M6 13l6 6 6-6",
        ["arrow-left"] = ArrowLeft,
        ["arrow-right"] = ArrowRight,
        ["chevron-right"] = "M9 6l6 6-6 6",
        ["info"] = Info,
        ["warning"] = Warning,
        ["alert-triangle"] = Warning,
        ["error"] = Error,
        ["alert-circle"] = Error,
        ["star"] = Star,
        ["star-half"] = StarHalf,
        ["clock"] = Circle + "M12 7v5l3 2",
        ["calendar"] = Calendar,
        ["user"] = User,
        ["file"] = "M14 3H7a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1V7l-4-4zM14 3v4h4M9 13h6M9 17h6",
        ["folder"] = Folder,
        ["link"] = "M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1",
        ["external-link"] = "M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5",
        ["search"] = Search,
        ["settings"] = Settings,
        ["git-branch"] = "M6 4v12M18 8a2 2 0 1 0 0-4 2 2 0 0 0 0 4zM6 20a2 2 0 1 0 0-4 2 2 0 0 0 0 4zM18 8c0 5-12 3-12 8",
        ["play"] = "M7 4.5v15l12-7.5-12-7.5z",
        ["pause"] = "M8 5v14M16 5v14",

        // A2UI basic-catalog names onto the set above. "help" has no match and stays unknown.
        ["arrowBack"] = ArrowLeft,
        ["arrowForward"] = ArrowRight,
        ["close"] = X,
        ["add"] = Plus,
        ["remove"] = Minus,
        ["starHalf"] = StarHalf,
        ["calendarToday"] = Calendar,
        ["person"] = User,
    };

    /// <summary>SVG path <c>d</c> for a built-in icon name (case-sensitive, ordinal); null when unknown.</summary>
    public static string? Path(string? name) =>
        name is not null && Paths.TryGetValue(name, out var d) ? d : null;
}

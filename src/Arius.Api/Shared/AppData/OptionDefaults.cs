namespace Arius.Api.AppData;

/// <summary>
/// The defaults hierarchy for the five archive/restore options: factory, then global settings, then repository
/// properties (a run's own choice is layered on top by the caller). Pure model and rules; storage lives in
/// <see cref="AppDatabase"/>.
/// </summary>
internal static class OptionDefaults
{
    public const string SourceFactory    = "factory";
    public const string SourceSettings   = "settings";
    public const string SourceRepository = "repository";

    public static readonly DefaultValues Factory = new("archive", "keep", false, false, false);

    /// <summary>Option names as used on the wire and in DB rows.</summary>
    public static readonly IReadOnlyList<string> OptionNames = ["tier", "onDisk", "fastHash", "overwrite", "noPointers"];

    public static readonly IReadOnlyList<string> Tiers        = ["hot", "cool", "cold", "archive"];
    public static readonly IReadOnlyList<string> OnDiskValues = ["keep", "keep-pointers", "replace"];

    /// <summary>Takes each option from the first level that sets it: repository, then global, then factory.</summary>
    public static ResolvedDefaults Resolve(DefaultValues global, DefaultValues repository)
    {
        ResolvedValue<string> Pick(Func<DefaultValues, string?> get) =>
            get(repository) is { } r ? new(r, SourceRepository)
            : get(global)   is { } g ? new(g, SourceSettings)
            : new(get(Factory)!, SourceFactory);

        ResolvedValue<bool> PickBool(Func<DefaultValues, bool?> get) =>
            get(repository) is { } r ? new(r, SourceRepository)
            : get(global)   is { } g ? new(g, SourceSettings)
            : new(get(Factory)!.Value, SourceFactory);

        return new ResolvedDefaults(
            Pick(v => v.Tier),
            Pick(v => v.OnDisk),
            PickBool(v => v.FastHash),
            PickBool(v => v.Overwrite),
            PickBool(v => v.NoPointers));
    }

    /// <summary>Returns <c>null</c> when every set value is allowed; otherwise a message naming the option and the bad value.</summary>
    public static string? Validate(DefaultValues values)
    {
        if (values.Tier is { } tier && !Tiers.Contains(tier))
            return $"Unknown tier '{tier}'; expected one of: {string.Join(", ", Tiers)}.";
        if (values.OnDisk is { } onDisk && !OnDiskValues.Contains(onDisk))
            return $"Unknown onDisk '{onDisk}'; expected one of: {string.Join(", ", OnDiskValues)}.";
        return null;
    }

    /// <summary>Maps an <c>onDisk</c> value to the archive command's flags.</summary>
    public static (bool RemoveLocal, bool WritePointers) ToArchiveFlags(string onDisk) => onDisk switch
    {
        "keep"          => (false, false),
        "keep-pointers" => (false, true),
        "replace"       => (true, true),
        _               => throw new ArgumentOutOfRangeException(nameof(onDisk), onDisk, "Unknown onDisk value."),
    };

    /// <summary>The set (non-null) options as DB rows; booleans as <c>"true"</c>/<c>"false"</c>.</summary>
    public static IEnumerable<(string Option, string Value)> ToRows(DefaultValues values)
    {
        if (values.Tier is { } tier)           yield return ("tier", tier);
        if (values.OnDisk is { } onDisk)       yield return ("onDisk", onDisk);
        if (values.FastHash is { } fastHash)   yield return ("fastHash", Bool(fastHash));
        if (values.Overwrite is { } overwrite) yield return ("overwrite", Bool(overwrite));
        if (values.NoPointers is { } noPointers) yield return ("noPointers", Bool(noPointers));

        static string Bool(bool value) => value ? "true" : "false";
    }

    /// <summary>Inverse of <see cref="ToRows"/>; options without a row stay unset.</summary>
    public static DefaultValues FromRows(IEnumerable<(string Option, string Value)> rows)
    {
        var values = DefaultValues.None;
        foreach (var (option, value) in rows)
            values = option switch
            {
                "tier"       => values with { Tier = value },
                "onDisk"     => values with { OnDisk = value },
                "fastHash"   => values with { FastHash = bool.Parse(value) },
                "overwrite"  => values with { Overwrite = bool.Parse(value) },
                "noPointers" => values with { NoPointers = bool.Parse(value) },
                _            => throw new ArgumentOutOfRangeException(nameof(rows), option, "Unknown option."),
            };
        return values;
    }
}

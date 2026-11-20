namespace Codery.Village.Server.Desk;

/// <summary>Set under <c>Village:Desk</c>; the switch itself is <c>Village:Tasks:Enabled</c>.</summary>
public sealed record DeskOptions
{
    public bool Enabled { get; init; }
    public string Repository { get; init; } = "Coderynl/codery-village";
    public string AppId { get; init; } = "";
    /// <summary>The App's private key, as PEM.</summary>
    public string PrivateKey { get; init; } = "";
    /// <summary>Eddie's bot login.</summary>
    public string EddieLogin { get; init; } = "";
    /// <summary>Who gets mentioned.</summary>
    public string Owner { get; init; } = "Codery-nl";

    public static DeskOptions From(IConfiguration configuration)
    {
        var options = configuration.GetSection("Village:Desk").Get<DeskOptions>() ?? new DeskOptions();
        return options with { Enabled = configuration.GetValue<bool>("Village:Tasks:Enabled") };
    }
}

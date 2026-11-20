using System.Globalization;
using System.Text;

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

    // the key never shows up in a log or an error: only whether there is one
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture,
            $"Enabled = {Enabled}, Repository = {Repository}, AppId = {AppId}, PrivateKey = {(string.IsNullOrEmpty(PrivateKey) ? "none" : "set")}, EddieLogin = {EddieLogin}, Owner = {Owner}");
        return true;
    }
}

using System.Text;
using System.Text.Json;

namespace FootballSim.Core;

/// <summary>What a player can do beyond his ratings. In a player file, <c>traits</c> is a list of these names in snake_case.</summary>
[Flags]
public enum PlayerTrait
{
    None = 0,
    /// <summary><c>outside_foot</c>: the outside of his strong foot reaches further toward that foot's side (see ShotModel.OutsideCapDeg).</summary>
    OutsideFoot = 1,
}

/// <summary>A player's ratings and body. Ratings are integers 1 to 99.</summary>
public sealed class PlayerSheet
{
    /// <summary>Names of the integer rating fields, in file order.</summary>
    public static readonly IReadOnlyList<string> RatingNames = new[]
    {
        "acceleration", "sprint_speed", "agility", "balance",
        "close_dribbling", "sprint_dribbling", "ball_control", "first_touch", "skill_moves",
        "finishing", "long_shots", "shot_power", "curve", "composure", "weak_foot",
        "reflexes", "diving", "reach", "positioning", "handling", "rushing_out",
    };

    /// <summary>Ratings that only the keeper model reads.</summary>
    public static readonly IReadOnlyList<string> KeeperRatingNames = new[]
        { "reflexes", "diving", "reach", "positioning", "handling", "rushing_out" };

    public const float HeightMin = 1.60f, HeightMax = 2.05f, WeightMin = 55f, WeightMax = 100f;
    public const int RatingDefault = 50;

    public string Id { get; set; } = "unknown";
    public string Name { get; set; } = "Unknown";

    public int Acceleration { get; set; } = RatingDefault;
    public int SprintSpeed { get; set; } = RatingDefault;
    public int Agility { get; set; } = RatingDefault;
    public int Balance { get; set; } = RatingDefault;
    /// <summary>Dribbling at the feet: the close style's cadence and top speed, turns, the early touch after a turn, and every touch that is not a sprint push or a knock-on.</summary>
    public int CloseDribbling { get; set; } = RatingDefault;
    /// <summary>Dribbling at speed: the sprint cadence, the speed style's top speed, sprint pushes and knock-ons.</summary>
    public int SprintDribbling { get; set; } = RatingDefault;
    public int BallControl { get; set; } = RatingDefault;
    public int FirstTouch { get; set; } = RatingDefault;
    /// <summary>Skill moves: their exit burst and how much survives a chain. A file without it uses <see cref="CloseDribbling"/>.</summary>
    public int SkillMoves { get; set; } = RatingDefault;
    public int Finishing { get; set; } = RatingDefault;
    public int LongShots { get; set; } = RatingDefault;
    public int ShotPower { get; set; } = RatingDefault;
    public int Curve { get; set; } = RatingDefault;
    public int Composure { get; set; } = RatingDefault;
    /// <summary>
    /// How good the weaker foot is, 1 to 99 like every rating: a strike or touch with it is the wider, slower and flatter the
    /// lower this is, and costs nothing at 99.
    /// </summary>
    public int WeakFoot { get; set; } = RatingDefault;
    public int Reflexes { get; set; } = RatingDefault;
    public int Diving { get; set; } = RatingDefault;
    public int Reach { get; set; } = RatingDefault;
    public int Positioning { get; set; } = RatingDefault;
    public int Handling { get; set; } = RatingDefault;
    public int RushingOut { get; set; } = RatingDefault;
    public float HeightM { get; set; } = 1.80f;
    public float WeightKg { get; set; } = 75f;
    public Foot PreferredFoot { get; set; } = Foot.Right;
    public PlayerTrait Traits { get; set; }

    /// <summary>The traits by their file names, in file order.</summary>
    public static readonly IReadOnlyList<(string Name, PlayerTrait Trait)> TraitNames = new[] { ("outside_foot", PlayerTrait.OutsideFoot) };

    public bool Has(PlayerTrait trait) => (Traits & trait) != 0;

    /// <summary>Reads a rating by its file name, for example <c>"finishing"</c>. Throws for an unknown name.</summary>
    public int GetRating(string name) => name switch
    {
        "acceleration" => Acceleration, "sprint_speed" => SprintSpeed, "agility" => Agility, "balance" => Balance,
        "close_dribbling" => CloseDribbling, "sprint_dribbling" => SprintDribbling,
        "ball_control" => BallControl, "first_touch" => FirstTouch, "skill_moves" => SkillMoves,
        "finishing" => Finishing, "long_shots" => LongShots, "shot_power" => ShotPower, "curve" => Curve,
        "composure" => Composure, "weak_foot" => WeakFoot,
        "reflexes" => Reflexes, "diving" => Diving, "reach" => Reach, "positioning" => Positioning,
        "handling" => Handling, "rushing_out" => RushingOut,
        _ => throw new ArgumentException($"Unknown rating '{name}'", nameof(name)),
    };

    /// <summary>Sets a rating by its file name (clamped to its range). Throws for an unknown name.</summary>
    public void SetRating(string name, int value)
    {
        int v = Math.Clamp(value, 1, 99);
        switch (name)
        {
            case "acceleration": Acceleration = v; break;
            case "sprint_speed": SprintSpeed = v; break;
            case "agility": Agility = v; break;
            case "balance": Balance = v; break;
            case "close_dribbling": CloseDribbling = v; break;
            case "sprint_dribbling": SprintDribbling = v; break;
            case "ball_control": BallControl = v; break;
            case "first_touch": FirstTouch = v; break;
            case "skill_moves": SkillMoves = v; break;
            case "finishing": Finishing = v; break;
            case "long_shots": LongShots = v; break;
            case "shot_power": ShotPower = v; break;
            case "curve": Curve = v; break;
            case "composure": Composure = v; break;
            case "weak_foot": WeakFoot = v; break;
            case "reflexes": Reflexes = v; break;
            case "diving": Diving = v; break;
            case "reach": Reach = v; break;
            case "positioning": Positioning = v; break;
            case "handling": Handling = v; break;
            case "rushing_out": RushingOut = v; break;
            default: throw new ArgumentException($"Unknown rating '{name}'", nameof(name));
        }
    }

    /// <summary>A copy that can be changed without touching this sheet.</summary>
    public PlayerSheet Clone() => (PlayerSheet)MemberwiseClone();

    /// <summary>The sheet as indented JSON in the file format.</summary>
    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("id", Id);
            w.WriteString("name", Name);
            foreach (var n in RatingNames) w.WriteNumber(n, GetRating(n));
            w.WriteNumber("height_m", HeightM);
            w.WriteNumber("weight_kg", WeightKg);
            w.WriteString("preferred_foot", PreferredFoot == Foot.Left ? "left" : "right");
            if (Traits != PlayerTrait.None)
            {
                w.WriteStartArray("traits");
                foreach (var (name, trait) in TraitNames) if (Has(trait)) w.WriteStringValue(name);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

namespace Butler;

// A kind of trigger: the value of "on" in the triggers file. To add one, write a class in this folder and list it in All.
// The tray polls each kind (Poll, every Interval).
public abstract class TriggerKind
{
    public static readonly TriggerKind[] All = [new ScheduleTrigger(), new MailTrigger(), new AdoTrigger(), new FileTrigger()];

    public static TriggerKind? Find(string? on) => All.FirstOrDefault(k => k.Name == on);

    public abstract string Name { get; }
    public abstract string Label { get; }

    // Its own fields in a trigger entry, besides name, on and prompt, in the order they are written to the file;
    // also the form of the dashboard's trigger editor.
    public abstract FormField[] Form { get; }

    // The fields that land in Trigger.Fields (schedule and from are typed on Trigger).
    public string[] Fields => [.. Form.Select(f => f.Name).Except(Trigger.Typed)];

    // The ${...} its prompts can use (lower case), and what a prompt using none of them gets appended.
    public virtual string[] Variables => [];
    public virtual string? Context => null;

    // Throws a FormatException when the trigger's own fields are wrong.
    public abstract void Validate(Trigger t);

    // When it runs, in one line for the dashboard.
    public abstract string Describe(Trigger t);

    // How often the tray polls it.
    public virtual TimeSpan Interval => TimeSpan.FromSeconds(30);

    // The runs to start since the last poll (trigger + variable values), for the triggers of this kind.
    // Keeps its own checkpoint in stateDir; the first poll starts from now. Runs on the tray's polling thread (STA).
    public virtual List<(Trigger Trigger, Dictionary<string, string> Values)> Poll(List<Trigger> triggers, Settings settings, string stateDir) => [];
}

// A field of a trigger kind in the editor. Type: "text", "list" (one value per line), "folder", "schedule".
public sealed record FormField(string Name, string Label, string Type, string Hint = "", bool Optional = false);

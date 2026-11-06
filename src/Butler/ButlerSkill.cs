using System.Text;

namespace Butler;

// The "butler" skill, written to the skills folder at each start (TrayApp.Reload): how an agent configures Butler, so that
// "add a Butler trigger that..." works in any Claude session. Made from the kinds (Help, Form, Variables, Context) and the
// settings, in English. A SKILL.md there without the marker line is the user's own: left alone.
public static class ButlerSkill
{
    public const string Marker = "<!-- Written by Butler at each start, from its code: changes here are lost. Delete this line to keep your own version. -->";

    public static string PathIn(string skillsFolder) => Path.Combine(skillsFolder, "butler", "SKILL.md");

    // Writes it when it changed (the skills folder is a git repo: no needless change). False when the file is the user's.
    public static bool Write(string skillsFolder, Settings settings, string exe)
    {
        var path = PathIn(skillsFolder);
        var text = Text(settings, exe);
        if (File.Exists(path))
        {
            var old = File.ReadAllText(path);
            if (!old.Contains(Marker)) return false;
            if (old.ReplaceLineEndings("\n") == text) return true;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return true;
    }

    public static string Text(Settings settings, string exe) => Lang.English(() =>
    {
        var kinds = new StringBuilder();
        foreach (var k in TriggerKind.All)
        {
            kinds.Append($"\n### `\"on\": \"{k.Name}\"` ({k.Label})\n\n{k.Help}\n\n");
            foreach (var f in k.Form)
                kinds.Append($"- `{f.Name}`{(f.Optional ? " (optional)" : "")}: " + f.Type switch
                {
                    "list" => "a list of strings",
                    "check" => "true or false",
                    _ => "a string",
                } + (f.Hint.Length > 0 ? $". {f.Hint}" : "") + "\n");
            kinds.Append(k.Variables.Length == 0 ? "\nNo variables.\n"
                : $"\nVariables: {string.Join(", ", k.Variables.Select(v => $"`${{{v}}}`"))}" +
                  (k.Context is { } c ? $". A prompt using none of them gets this appended: `{c.Replace("\n", " ")}`.\n" : ".\n"));
        }
        var levels = string.Join(", ", Settings.Levels.Select(l => $"`{l}` = {settings.ModelFor(l) ?? "the CLI's default"}"));
        return $$"""
            ---
            name: butler
            description: Configure Butler, the tray app that runs Claude Code (or Copilot) prompts on triggers - add, change or remove triggers in butler.json (schedules, Outlook mails, Azure DevOps work items, new files, hotkeys on a selected text), pick their model, check the file. Use when the user asks Butler to do something on a schedule or an event, to set up a hotkey, or about Butler's triggers, models or settings.
            ---
            {{Marker}}

            # Butler

            Butler runs a prompt (or a command line) when a trigger fires and shows every run in its dashboard. Its triggers are in
            **{{settings.TriggersFile}}**: a JSON array, one object per trigger. Butler reloads the file as soon as it changes.

            Edit that file, keeping its layout. **After every change, check it:**

                & "{{exe}}" check | Out-String

            (its output must be piped). It prints what is wrong, or how many triggers it read. A file with an error is not applied:
            no trigger runs until it is fixed.

            ## A trigger

            - `name`: letters, digits, `-` and `_`; unique (any case). It is how the dashboard and the runs name it.
            - `on`: its kind (below), then the kind's own fields; any other field is refused.
            - `prompt`: what the agent gets, run unattended from the user folder, nobody to talk to. Start with `/<skill>` to call
              a skill. Use the kind's `${variables}`; Butler itself tells the agent how to ask the user a question
              (the run then waits for an answer in the dashboard), the prompt only says what to do.
            - or `command` instead of `prompt`: a command line run by the shell (cmd on Windows, sh elsewhere), no agent. Exit
              code 0 is done, its last output line the summary. The event comes as environment variables (`BUTLER_SUBJECT`...):
              write `!BUTLER_SUBJECT!` with cmd (never `%BUTLER_SUBJECT%`: a value could run commands), `"$BUTLER_SUBJECT"`
              with sh. `${...}` is refused in a command.
            - `model` (prompts only, optional): `fast`, `balanced` or `powerful`, set to a model of each CLI on Butler's Settings
              page (with {{settings.Cli}} now: {{levels}}); or a model name, passed as is. None: the CLI's default. Pick the
              lightest that does the job: a simple check runs faster and cheaper on `fast`.

            ## Kinds
            {{kinds}}
            ## Example

            ```json
            [
              {
                "name": "morning-brief",
                "on": "schedule",
                "schedule": "mon-fri 08:30",
                "model": "balanced",
                "prompt": "/morning-brief"
              },
              {
                "name": "invoices",
                "on": "file",
                "folder": "%USERPROFILE%\\Downloads",
                "filter": "*.pdf",
                "prompt": "Read the invoice ${path} and add it to the expenses sheet."
              },
              {
                "name": "fix-text",
                "on": "hotkey",
                "keys": "ctrl+shift+space",
                "model": "fast",
                "prompt": "Fix the spelling and grammar of this text, keep its language and tone:\n${text}"
              }
            ]
            ```

            ## Settings

            Butler's own settings are in {{Program.SettingsFile}}, usually changed on its Settings page; edit them only when the
            user asks. `cli` (`claude` or `copilot`) runs the triggers; `models` maps each level to a model name per CLI
            (empty: the CLI's default). The Todoist token is not there: it is in the system's secret store.

            """.Replace("\r\n", "\n");
    });
}

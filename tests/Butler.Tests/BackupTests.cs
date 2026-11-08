using System.IO.Compression;

namespace Butler.Tests;

public class BackupTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("butler-backup-").FullName;
    string Skills => Path.Combine(dir, "skills");
    string Out => Path.Combine(dir, "backups");
    string SettingsFile => Path.Combine(dir, "settings.json");
    Settings Mine => new() { SkillsFolder = Skills, TriggersFile = Path.Combine(Skills, "butler.json"), BackupFolder = Out };

    public BackupTests()
    {
        Directory.CreateDirectory(Path.Combine(Skills, "brief"));
        Directory.CreateDirectory(Path.Combine(Skills, ".git"));
        File.WriteAllText(Path.Combine(Skills, "butler.json"), "[]");
        File.WriteAllText(Path.Combine(Skills, "brief", "SKILL.md"), "# brief");
        File.WriteAllText(Path.Combine(Skills, ".git", "HEAD"), "ref: refs/heads/main");
        File.WriteAllText(SettingsFile, "{}");
    }

    static string[] Entries(string zip)
    {
        using var z = ZipFile.OpenRead(zip);
        return [.. z.Entries.Select(e => e.FullName).Order()];
    }

    // Every file of the configuration as last changed long ago, so that only what a test changes is new.
    void Age()
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(Out)))
            File.SetLastWriteTime(f, DateTime.Now.AddDays(-10));
    }

    [Fact]
    public void The_zip_has_the_skills_and_the_settings_without_git()
    {
        var zip = Backup.Write(Mine, SettingsFile, DateTime.Now)!;
        Assert.Equal(["settings.json", "skills/brief/SKILL.md", "skills/butler.json"], Entries(zip.File));
        Assert.Equal(zip, Backup.Last(Mine));
    }

    [Fact]
    public void A_triggers_file_outside_the_skills_folder_is_added()
    {
        var triggers = Path.Combine(dir, "elsewhere", "butler.json");
        Directory.CreateDirectory(Path.GetDirectoryName(triggers)!);
        File.WriteAllText(triggers, "[]");
        var zip = Backup.Write(Mine with { TriggersFile = triggers }, SettingsFile, DateTime.Now)!;
        Assert.Contains("butler.json", Entries(zip.File));
    }

    [Fact]
    public void Due_once_a_day_and_only_when_something_changed()
    {
        Age();
        var now = DateTime.Now;
        Assert.True(Backup.Due(Mine, SettingsFile, now)); // none yet
        Backup.Write(Mine, SettingsFile, now);
        Assert.False(Backup.Due(Mine, SettingsFile, now.AddDays(2))); // nothing changed

        File.WriteAllText(Path.Combine(Skills, "brief", "SKILL.md"), "# brief, changed");
        Assert.False(Backup.Due(Mine, SettingsFile, now.AddHours(1))); // less than a day
        Assert.True(Backup.Due(Mine, SettingsFile, now.AddDays(1)));

        Backup.Write(Mine, SettingsFile, now.AddDays(1));
        File.Delete(Path.Combine(Skills, "brief", "SKILL.md")); // a removed file: the zip has an entry too many
        Assert.True(Backup.Due(Mine, SettingsFile, now.AddDays(3)));
    }

    [Fact]
    public void Only_the_last_ones_are_kept_and_other_files_are_left()
    {
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Out, "notes.txt"), "mine");
        var start = DateTime.Now.AddDays(-40);
        for (var i = 0; i < Backup.Keep + 3; i++) Backup.Write(Mine, SettingsFile, start.AddDays(i));
        Assert.Equal(Backup.Keep, Directory.GetFiles(Out, "Butler-*.zip").Length);
        Assert.True(File.Exists(Path.Combine(Out, "notes.txt")));
        Assert.Equal(start.AddDays(Backup.Keep + 2).ToString("s"), Backup.Last(Mine)!.At.ToString("s"));
    }

    [Fact]
    public void Without_a_folder_there_is_no_backup()
    {
        var off = Mine with { BackupFolder = "" };
        Assert.False(Backup.Due(off, SettingsFile, DateTime.Now));
        Assert.Null(Backup.Write(off, SettingsFile, DateTime.Now));
        Assert.Null(Backup.Last(off));
        Assert.False(Directory.Exists(Out));
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);
}

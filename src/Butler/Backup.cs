using System.Globalization;
using System.IO.Compression;

namespace Butler;

public sealed record BackupFile(string File, DateTime At);

// A zip of Butler's configuration in Settings.BackupFolder: the skills folder (without .git) as skills/, the triggers file
// when it is elsewhere, and settings.json. At most once a day and only when something changed; the last Keep zips are kept.
// Not the secrets (in the OS's store), the runs or the checkpoints. Restoring is unzipping.
public static class Backup
{
    public const int Keep = 30;
    const string Prefix = "Butler-", Format = "yyyy-MM-dd-HHmmss";

    public static string Folder(Settings s) => Environment.ExpandEnvironmentVariables(s.BackupFolder);

    // Butler's zips in the folder, oldest first (by the time in their name: other files are never touched).
    static List<BackupFile> Zips(string folder) => !Directory.Exists(folder) ? [] : [..
        Directory.EnumerateFiles(folder, Prefix + "*.zip")
            .Select(f => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f)[Prefix.Length..], Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? new BackupFile(f, at) : null)
            .OfType<BackupFile>()
            .OrderBy(z => z.At)];

    public static BackupFile? Last(Settings s) => s.BackupFolder == "" ? null : Zips(Folder(s)).LastOrDefault();

    // Whether a new zip is due: none yet, or the last one is a day old and a file was changed, added or removed since.
    public static bool Due(Settings s, string settingsFile, DateTime now)
    {
        if (s.BackupFolder == "") return false;
        if (Last(s) is not { } last) return true;
        if (now - last.At < TimeSpan.FromDays(1)) return false;
        var files = Files(s, settingsFile).ToList();
        if (files.Any(f => File.GetLastWriteTime(f.Source) > last.At)) return true;
        try
        {
            using var zip = ZipFile.OpenRead(last.File);
            return !zip.Entries.Select(e => e.FullName).ToHashSet().SetEquals(files.Select(f => f.Entry));
        }
        catch (InvalidDataException) { return true; } // not a readable zip
    }

    // Writes a zip now and removes the oldest beyond Keep. Returns it, or null without a backup folder.
    public static BackupFile? Write(Settings s, string settingsFile, DateTime now)
    {
        if (s.BackupFolder == "") return null;
        var folder = Folder(s);
        Directory.CreateDirectory(folder);
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)); // as read back from its name
        var zip = new BackupFile(Path.Combine(folder, Prefix + now.ToString(Format, CultureInfo.InvariantCulture) + ".zip"), now);
        var tmp = zip.File + ".tmp";
        File.Delete(tmp);
        using (var archive = ZipFile.Open(tmp, ZipArchiveMode.Create))
            foreach (var (source, entry) in Files(s, settingsFile))
            {
                try { archive.CreateEntryFromFile(source, entry); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // locked or gone: the others are saved
            }
        File.Move(tmp, zip.File, overwrite: true);
        foreach (var old in Zips(folder).SkipLast(Keep)) File.Delete(old.File);
        return zip;
    }

    // Whether path is folder or inside it.
    public static bool Inside(string path, string folder)
    {
        var (p, f) = (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // What goes in the zip: the file and its name there.
    static IEnumerable<(string Source, string Entry)> Files(Settings s, string settingsFile)
    {
        var skills = s.SkillsFolder == "" ? null : Path.GetFullPath(s.SkillsFolder);
        if (Directory.Exists(skills))
            foreach (var f in Walk(skills)) yield return (f, "skills/" + Path.GetRelativePath(skills, f).Replace('\\', '/'));
        if (File.Exists(s.TriggersFile) && (skills == null || !Inside(s.TriggersFile, skills))) yield return (Path.GetFullPath(s.TriggersFile), Path.GetFileName(s.TriggersFile));
        if (File.Exists(settingsFile)) yield return (settingsFile, "settings.json");
    }

    static IEnumerable<string> Walk(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir)) yield return f;
        foreach (var d in Directory.EnumerateDirectories(dir).Where(d => Path.GetFileName(d) != ".git"))
            foreach (var f in Walk(d)) yield return f;
    }
}

using System.Text.Json;

namespace Butler.Tests;

public class SkillsTests
{
    [Fact]
    public void Lists_skill_folders_with_their_front_matter()
    {
        var dir = Directory.CreateTempSubdirectory("skills-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "triage-mail"));
            File.WriteAllText(Path.Combine(dir, "triage-mail", "SKILL.md"), "---\r\nname: triage-mail\r\ndescription: Turn alert mails into tickets.\r\n---\r\n\r\n# Body\r\nname: not this\r\n");
            Directory.CreateDirectory(Path.Combine(dir, "bare"));
            File.WriteAllText(Path.Combine(dir, "bare", "SKILL.md"), "# no front matter");
            Directory.CreateDirectory(Path.Combine(dir, "not-a-skill"));
            File.WriteAllText(Path.Combine(dir, "butler.json"), "[]");

            Assert.Equal([new Skill("bare", "", "bare"), new Skill("triage-mail", "Turn alert mails into tickets.", "triage-mail")], Skills.Load(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    // Claude Code's plugins: enabled in the user's settings and installed for the user, their skills called /<plugin>:<skill>.
    [Fact]
    public void Lists_the_skills_of_the_user_s_enabled_claude_plugins()
    {
        var home = Directory.CreateTempSubdirectory("home-").FullName;
        try
        {
            var skills = Path.Combine(home, ".claude", "skills");
            Directory.CreateDirectory(Path.Combine(skills, "brief"));
            File.WriteAllText(Path.Combine(skills, "brief", "SKILL.md"), "---\nname: brief\n---\n");
            Assert.Empty(new ClaudeCli().Plugins(home)); // no plugin files

            string Plugin(string name)
            {
                var dir = Path.Combine(home, ".claude", "plugins", "cache", "market", name, "1.0.0");
                Directory.CreateDirectory(Path.Combine(dir, "skills", name + "-ohris"));
                File.WriteAllText(Path.Combine(dir, "skills", name + "-ohris", "SKILL.md"), $"---\nname: {name}-ohris\ndescription: Fills OHRIS.\n---\n");
                return dir;
            }
            string tools = Plugin("tools"), off = Plugin("off"), project = Plugin("project");
            File.WriteAllText(Path.Combine(home, ".claude", "settings.json"), JsonSerializer.Serialize(new
            {
                enabledPlugins = new Dictionary<string, bool> { ["tools@market"] = true, ["off@market"] = false, ["project@market"] = true, ["gone@market"] = true },
            }));
            File.WriteAllText(Path.Combine(home, ".claude", "plugins", "installed_plugins.json"), JsonSerializer.Serialize(new
            {
                version = 2,
                plugins = new Dictionary<string, object[]>
                {
                    ["tools@market"] = [new { scope = "user", installPath = tools }],
                    ["off@market"] = [new { scope = "user", installPath = off }],
                    ["project@market"] = [new { scope = "project", projectPath = "C:\\elsewhere", installPath = project }],
                },
            }));

            Assert.Equal([("tools", tools)], new ClaudeCli().Plugins(home));
            Assert.Empty(new CopilotCli().Plugins(home));
            var folder = Path.Combine(tools, "skills", "tools-ohris");
            Assert.Equal([new Skill("brief", "", "brief"), new Skill("tools:tools-ohris", "Fills OHRIS.", folder, "tools")], Skills.Load(skills, new ClaudeCli().Plugins(home)));
            Assert.Null(Skills.FolderOf(skills, folder)); // not the user's to trash

            File.WriteAllText(Path.Combine(home, ".claude", "settings.json"), "{ broken");
            Assert.Empty(new ClaudeCli().Plugins(home));
        }
        finally { Directory.Delete(home, true); }
    }

    [Theory]
    [InlineData("/triage-mail", "triage-mail")]
    [InlineData("/timesheet fill the month", "timesheet")]
    [InlineData("/anthropic-skills:docx make it", "anthropic-skills:docx")]
    [InlineData("Check my mails", null)]
    public void Finds_the_invoked_skill(string prompt, string? skill) => Assert.Equal(skill, Skills.Invoked(prompt));

    // The page names the skill to delete by its folder: only one of the skills folder's skills may come out of it.
    [Fact]
    public void Only_a_skill_folder_of_the_skills_folder_can_be_named()
    {
        var dir = Directory.CreateTempSubdirectory("skills-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "brief"));
            File.WriteAllText(Path.Combine(dir, "brief", "SKILL.md"), "---\nname: brief\n---\n");
            Directory.CreateDirectory(Path.Combine(dir, "notes")); // no SKILL.md: not a skill

            Assert.Equal(Path.Combine(dir, "brief"), Skills.FolderOf(dir, "brief"));
            foreach (var bad in new[] { "notes", "", ".", "..", "../brief", "brief/..", dir, Path.Combine(dir, "brief"), "missing" })
                Assert.Null(Skills.FolderOf(dir, bad));
        }
        finally { Directory.Delete(dir, true); }
    }
}
